using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bicep.Core;
using Bicep.Core.Emit;
using Bicep.Core.FileSystem;
using Bicep.Core.Parsing;
using Bicep.Core.Registry;
using Bicep.Core.Resources;
using Bicep.Core.Semantics;
using Bicep.Core.Syntax;
using Bicep.Core.TypeSystem;
using Bicep.Core.TypeSystem.Az;
using Bicep.Core.Workspaces;
using Microsoft.Tye;
using Microsoft.Tye.ConfigModel;
using Microsoft.Tye.Hosting;
using ObjectType = Bicep.Core.TypeSystem.ObjectType;
using ResourceType = Bicep.Core.TypeSystem.ResourceType;

namespace Boop.Cli
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var devCommmand = new Command("dev")
            {
                Handler = CommandHandler.Create(() =>
                {
                    var input = GetInput();
                    var env = EnsureEnvironment();
                    var model = DeployResources(env, input, out var resourceMap);

                    foreach (var app in GetApps(model))
                    {
                        var settings = CollectSettings(app, resourceMap);
                        AssignIdentity(resourceMap, app, env.UserName, false);
                        SetProjectSettings(app, settings);
                    }
                })
            };

            var runCommand = new Command("run")
            {
                Handler = CommandHandler.Create<RunCommandArguments>(async args =>
                {
                    var input = GetInput();

                    var output = new OutputContext(args.Console, args.Verbosity);

                    output.WriteInfoLine("Loading Application Details...");

                    var filter = ApplicationFactoryFilter.GetApplicationFactoryFilter(args.Tags);

                    var model = GetSemanticModel(input);

                    var app = BuildConfigApplication(input, GetApps(model), null, model);

                    var application = await ApplicationFactory.CreateAsync(output, new FileInfo(input), app, args.Framework, filter);
                    if (application.Services.Count == 0)
                    {
                        throw new CommandException($"No services found in \"{application.Source.Name}\"");
                    }

                    var options = new HostOptions()
                    {
                        Dashboard = args.Dashboard,
                        Docker = args.Docker,
                        NoBuild = args.NoBuild,
                        Port = args.Port,

                        // parsed later by the diagnostics code
                        DistributedTraceProvider = args.Dtrace,
                        LoggingProvider = args.Logs,
                        MetricsProvider = args.Metrics,
                        LogVerbosity = args.Verbosity,
                        Watch = args.Watch
                    };
                    options.Debug.AddRange(args.Debug);

                    InitializeThreadPoolSettings(application.Services.Count);

                    output.WriteInfoLine("Launching Tye Host...");
                    output.WriteInfoLine(string.Empty);

                    await using var host = new TyeHost(application.ToHostingApplication(), options);
                    await host.RunAsync();
                })
            };

            var deployCommand = new Command("deploy")
            {
                Handler = CommandHandler.Create<DeployCommandArguments>((args) =>
                {
                    var input = GetInput();
                    var env = EnsureEnvironment();
                    var model = DeployResources(env, input, out var resourceMap);

                    foreach (var app in GetApps(model).GroupBy(app => app.Resource))
                    {
                        if (app.Key == null)
                        {
                            continue;
                        }

                        var output = new OutputContext(args.Console, args.Verbosity);
                        var resourceType = ((ResourceType)app.Key.Type).TypeReference.FullyQualifiedType;
                        if (resourceType == "Microsoft.ContainerService/managedClusters")
                        {
                            DeployAKS(input, env, app, resourceMap, output, model);
                        }
                        else
                        {
                            if (app.Count() > 1)
                            {
                                throw new InvalidOperationException($"Can't deploy multiple apps to {resourceType}");
                            }

                            DeploySingleApp(env, app.Single(), resourceMap, CollectSettings(app.Single(), resourceMap));
                        }
                    }
                })
            };

            var envCommand = new Command("env")
            {
                Handler = CommandHandler.Create(() =>
                {
                    Console.WriteLine(ReadEnvironment());
                })
            };

            envCommand.Add(new Command("setup")
            {
                Handler = CommandHandler.Create(() => SetEnvironment())
            });


            var rootCommand = new RootCommand()
            {
                devCommmand,
                envCommand,
                deployCommand,
                runCommand
            };

            return await rootCommand.InvokeAsync(args);
        }

        private static void DeployAKS(string input, BoopEnvironment env, IEnumerable<RegisteredApp> apps, List<DeployedResource> deployedResources, OutputContext output, SemanticModel model)
        {
            var deployedResource = deployedResources.Single(r => r.Resource.Name == apps.First().Resource.Name);

            var res = AzCli.Run<AksResource>($"aks show --resource-group {env.Name} --name {deployedResource.Name} --subscription {env.SubscriptionId}");

            var configApplication = BuildConfigApplication(input, apps, deployedResources, model);

            foreach (var service in configApplication.Services)
            {
                service.Configuration.Add(new ConfigConfigurationSource()
                {
                    Name = "AZURE_CLIENT_ID",
                    Value = res.identityProfile.kubeletidentity.clientId
                });
            }

            var application = ApplicationFactory.CreateAsync(output, new FileInfo(input), configApplication).Result;
            application.Registry = new ContainerRegistry(GetRegistry(deployedResources, out var registryResource));

            AzCli.Run($"role assignment create --assignee {res.identityProfile.kubeletidentity.objectId} --role \"acrpull\" --scope {registryResource.Id}");
            AzCli.Run($"aks get-credentials --resource-group {env.Name} --name {deployedResource.Name} --subscription {env.SubscriptionId}");

            ExecuteDeployAsync(output, application, "production", true, false).Wait();

            foreach (var app in apps)
            {
                AssignIdentity(deployedResources, app, res.identityProfile.kubeletidentity.objectId, true);
            }

        }

        private static ConfigApplication BuildConfigApplication(string input, IEnumerable<RegisteredApp> apps, IList<DeployedResource> resources, SemanticModel model)
        {
            ConfigApplication app = new ConfigApplication
            {
                Name = Path.GetFileNameWithoutExtension(input).ToLower(),
                Source = new FileInfo(input)
            };

            foreach (var modelApp in apps)
            {
                var service = new ConfigService()
                {
                    Name = modelApp.Name
                };

                if (modelApp.IsFunction)
                {
                    service.AzureFunction = Path.GetDirectoryName(modelApp.Path);
                }
                else if (modelApp.IsDocker)
                {
                    service.DockerFile = modelApp.Path;
                    service.Bindings.Add(new ConfigServiceBinding()
                    {
                        Protocol = "http",
                        ContainerPort = 80
                    });
                }
                else
                {
                    service.Project = modelApp.Path;
                }

                if (resources != null)
                {
                    foreach (var setting in CollectSettings(modelApp, resources))
                    {
                        service.Configuration.Add(new ConfigConfigurationSource()
                        {
                            Name = setting.Key.Replace(":", "__"),
                            Value =  setting.Value
                        });
                    }
                }

                app.Services.Add(service);
            }

            app.Ingress = GetIngress(model).ToList();

            return app;
        }

        private static SemanticModel DeployResources(BoopEnvironment env, string input, out List<DeployedResource> resourceMap)
        {
            Console.WriteLine($"Deploying to subscription {env.SubscriptionId}, resource group {env.Name}");

            if (AzCli.Run<bool>($"group exists -n {env.Name} --subscription {env.SubscriptionId}") == false)
            {
                AzCli.Run($"group create -n {env.Name} --subscription {env.SubscriptionId} --location westus2");
            }

            var model = GetSemanticModel(input);

            var tempFile = Path.GetTempFileName();
            using (var fileStream = File.Create(tempFile))
            {
                new TemplateEmitter(model, "").Emit(fileStream);
            }

            var deploymentResult = AzCli.Run<JsonElement>($"deployment group create --resource-group {env.Name} --subscription {env.SubscriptionId} --template-file {tempFile}");

            resourceMap = CreateResourceMap(model, deploymentResult).ToList();

            foreach (var deployedResource in resourceMap)
            {
                Console.WriteLine("Deployed " + deployedResource.Resource.Name + " as " + deployedResource.Id);
            }

            return model;
        }

        private static IEnumerable<DeployedResource> CreateResourceMap(SemanticModel model, JsonElement deploymentResult)
        {
            foreach (var resourceMetadata in model.AllResources)
            {
                if (resourceMetadata.Parent != null) continue;

                var prop = deploymentResult.GetProperty("properties").GetProperty("outputs").GetProperty("_" + resourceMetadata.Symbol.Name).GetProperty("value");
                var id = prop.GetProperty("resourceId").GetString();
                var sid = prop.GetProperty("subscriptionId").GetString();
                var rg = prop.GetProperty("resourceGroupName").GetString();

                var fullId = $"/subscriptions/{sid}/resourceGroups/{rg}/providers/{id}";
                var name = id.Split("/").Last();

                yield return new DeployedResource(resourceMetadata.Symbol, fullId, name, prop);
            }
        }

        private static void DeploySingleApp(BoopEnvironment env, RegisteredApp app, IEnumerable<DeployedResource> deployedResources, Dictionary<string, string> settings)
        {
            var deployedResource = deployedResources.Single(r => r.Resource.Name == app.Resource.Name);
            if (app.IsDocker)
            {
                var workingDirectory = Path.GetDirectoryName(app.Path);
                var registry = GetRegistry(deployedResources, out _);

                var imageName = $"{registry}/{app.Name.ToLowerInvariant()}";
                var tag = "latest";

                Exec.Run("docker", $"build {workingDirectory} -f {app.Path} -t {imageName}:{tag}");
                Exec.Run("docker", $"push {imageName}:{tag}");
                AzCli.Run(
                    $"webapp config container set " +
                    $"--resource-group {env.Name} --name {deployedResource.Name} --subscription {env.SubscriptionId} " +
                    $"--docker-custom-image-name {imageName}:{tag} " +
                    $"--docker-registry-server-url https://{registry}");
            }
            else
            {

                var destinationArchiveFileName = BuildAndPackSingleApp(app);

                if (app.IsFunction)
                {
                     AzCli.Run($"functionapp config appsettings set --resource-group {env.Name} --name {deployedResource.Name} --subscription {env.SubscriptionId} --settings WEBSITE_RUN_FROM_PACKAGE=1 FUNCTIONS_EXTENSION_VERSION=~3");
                     AzCli.Run($"functionapp deployment source config-zip --resource-group {env.Name} --name {deployedResource.Name} --subscription {env.SubscriptionId} --src {destinationArchiveFileName}");
                }
                else
                {
                    AzCli.Run($"webapp config appsettings set --resource-group {env.Name} --name {deployedResource.Name} --subscription {env.SubscriptionId} --settings WEBSITE_RUN_FROM_PACKAGE=1");
                    AzCli.Run($"webapp deployment source config-zip --resource-group {env.Name} --name {deployedResource.Name} --subscription {env.SubscriptionId} --src {destinationArchiveFileName}");
                }
            }

            AssignRolesAndSettings(env, deployedResources, app, deployedResource, settings);

            Console.WriteLine($"{app.Path} published to {deployedResource.Id} view at http://{deployedResource.Properties.GetProperty("properties").GetProperty("hostNames")[0]}");
        }

        private static string BuildAndPackSingleApp(RegisteredApp app)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var destinationArchiveFileName = Path.GetTempFileName() + ".zip";

            if (app.Runtime == AppRuntimeType.Dotnet)
            {
                Exec.Run("dotnet", $"publish -c Release {app.Path} -o {tempDir}");
            }
            else if (app.Runtime == AppRuntimeType.NodeJs)
            {
                var appPackageRoot = Path.GetDirectoryName(app.Path);
                Exec.Run("cmd", "/c npm run-script --if-present build", appPackageRoot);
                tempDir = appPackageRoot;
            }

            ZipFile.CreateFromDirectory(tempDir, destinationArchiveFileName);

            return destinationArchiveFileName;
        }

        private static string GetRegistry(IEnumerable<DeployedResource> deployedResources, out DeployedResource registryResource)
        {
            foreach (var deployedResource in deployedResources)
            {
                var resourceType = ((ResourceType)deployedResource.Resource.Type);
                if (resourceType.TypeReference.FullyQualifiedType == "Microsoft.ContainerRegistry/registries")
                {
                    registryResource = deployedResource;
                    var registry = deployedResource.Properties.GetProperty("properties").GetProperty("loginServer").GetString();
                    AzCli.Run($"acr login -n {registry}");
                    return registry;
                }
            }

            throw new InvalidOperationException("Expected Microsoft.ContainerRegistry/registries resource defined as part of the deployment file");
        }

        private static void SetProjectSettings(RegisteredApp app, Dictionary<string,string> settings)
        {
            var workingDirectory = Path.GetDirectoryName(app.Path);
            if (app.IsFunction)
            {
                foreach (var setting in settings)
                {
                    Exec.Run("func", $"settings add {setting.Key.Replace(":", "__")} {setting.Value}", workingDirectory);
                }
            }
            else if (app.IsDocker)
            {
                var envFile = Path.Combine(workingDirectory, ".boop.env");
                File.WriteAllLines(envFile, settings.Select(s => $"{s.Key.Replace(":", "__")}={s.Value}"));
            }
            else
            {
                if (app.Runtime == AppRuntimeType.Dotnet)
                {
                    Exec.Run("dotnet", "user-secrets init", workingDirectory);
                    foreach (var setting in settings)
                    {
                        Exec.Run("dotnet", $"user-secrets set {setting.Key} {setting.Value}", workingDirectory);
                    }
                }
                else if (app.Runtime == AppRuntimeType.NodeJs)
                {
                    var envFile = Path.Combine(workingDirectory, ".env");
                    File.WriteAllLines(envFile, settings.Select(s => $"{s.Key.Replace(":", "__")}={s.Value}"));
                }
            }
        }

        private static Dictionary<string, string> CollectSettings(RegisteredApp app, IEnumerable<DeployedResource> deployedResources)
        {
            Dictionary<string, string> settings = new();
            foreach (var usage in app.Uses)
            {
                var deployedResource = deployedResources.Single(r => r.Resource.Name == usage.Resource.Name);

                var resourceType = ((ResourceType)deployedResource.Resource.Type);
                switch (resourceType.TypeReference.FullyQualifiedType)
                {
                    case "Microsoft.Storage/storageAccounts":
                        settings.Add($"{deployedResource.Resource.Name}:Blob:serviceUri",
                            deployedResource.Properties.GetProperty("properties").GetProperty("primaryEndpoints").GetProperty("blob").GetString());
                        settings.Add($"{deployedResource.Resource.Name}:Queue:serviceUri",
                            deployedResource.Properties.GetProperty("properties").GetProperty("primaryEndpoints").GetProperty("queue").GetString());
                        settings.Add($"{deployedResource.Resource.Name}:Table:serviceUri",
                            deployedResource.Properties.GetProperty("properties").GetProperty("primaryEndpoints").GetProperty("table").GetString());
                        break;

                    case "Microsoft.KeyVault/vaults":
                        settings.Add($"{deployedResource.Resource.Name}:vaultUri",
                            deployedResource.Properties.GetProperty("properties").GetProperty("vaultUri").GetString());
                        break;

                    case "Microsoft.ServiceBus/namespaces":
                        settings.Add($"{deployedResource.Resource.Name}:fullyQualifiedNamespace",
                            new Uri(deployedResource.Properties.GetProperty("properties").GetProperty("serviceBusEndpoint").GetString()).Host);
                        break;

                    case "Microsoft.Web/sites":
                        settings.Add($"{deployedResource.Resource.Name}:baseUrl",
                            "http://" + deployedResource.Properties.GetProperty("properties").GetProperty("hostNames")[0].GetString());
                        break;
                }
            }

            return settings;
        }

        private static void AssignRolesAndSettings(
            BoopEnvironment env,
            IEnumerable<DeployedResource> deployedResources,
            RegisteredApp app,
            DeployedResource appResource,
            Dictionary<string, string> settings)
        {
            Console.WriteLine("Assigning roles and settings");
            var res = AzCli.Run<ResourceIdentity>($"webapp identity assign --resource-group {env.Name} --name {appResource.Name} --subscription {env.SubscriptionId}");

            AssignIdentity(deployedResources, app, res.principalId, true);

            string settingsText = "";
            foreach (var setting in settings)
            {
                var key = setting.Key.Replace(":", "__");
                settingsText += $" {key}={setting.Value}";
            }

            if (!string.IsNullOrWhiteSpace(settingsText))
            {
                AzCli.Run($"webapp config appsettings set --resource-group {env.Name} --name {appResource.Name} --subscription {env.SubscriptionId} --settings {settingsText}");
            }
        }

        private static void AssignIdentity(IEnumerable<DeployedResource> deployedResources, RegisteredApp app, string identity, bool isServicePrincipal)
        {
            foreach (var usage in app.Uses)
            {
                var deployedResource = deployedResources.Single(r => r.Resource.Name == usage.Resource.Name);

                foreach (var role in usage.Roles)
                {
                    var assigneeParameter = isServicePrincipal ? $"--assignee-object-id {identity} --assignee-principal-type ServicePrincipal" : $"--assignee {identity}";
                    AzCli.Run($"role assignment create {assigneeParameter} --role \"{role}\" --scope {deployedResource.Id}");
                }
            }
        }

        private static BoopEnvironment EnsureEnvironment()
        {
            if (ReadEnvironment() is { } env) return env;

            Console.WriteLine("You have no environment configured, let's set it up!");
            var environment = SetEnvironment();

            return environment;
        }

        private static BoopEnvironment SetEnvironment()
        {
            var login = AzCli.CheckLogin();
            Console.WriteLine($"Please enter the subscription id to use [{login.id}({login.name})]:");
            var id = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(id))
            {
                id = login.id;
            }

            Console.WriteLine($"Please enter the environment name to use [{login.user.ShortName}]");
            var name = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = login.user.ShortName;
            }

            var environment = new BoopEnvironment(id, name, login.user.name);
            WriteEnvironment(environment);
            return environment;
        }

        private static BoopEnvironment ReadEnvironment()
        {
            var profile = GetProfilePath();
            if (File.Exists(profile))
            {
                return JsonSerializer.Deserialize<BoopEnvironment>(File.ReadAllText(profile));
            }

            return null;
        }

        private static string GetProfilePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".boop.json");
        }

        private static void WriteEnvironment(BoopEnvironment environment)
        {
            File.WriteAllText(GetProfilePath(), JsonSerializer.Serialize(environment));
        }

        private static IEnumerable<ConfigIngress> GetIngress(SemanticModel model)
        {
            foreach (var descendant in model.Root.Descendants)
            {
                if (descendant is ResourceSymbol resourceSymbol)
                {
                    var reference = resourceSymbol.TryGetResourceTypeReference();
                    if (!BoopResourceProvider.SameReference(reference, BoopResourceProvider.IngressResource.TypeReference))
                    {
                        continue;
                    }

                    var result = new ConfigIngress()
                    {
                        Name = resourceSymbol.Name
                    };

                    var body = resourceSymbol.DeclaringResource.GetBody();
                    foreach (var usesItem in ((ArraySyntax)body.SafeGetPropertyByName("endpoints")?.Value)?.Items ?? Array.Empty<ArrayItemSyntax>())
                    {
                        var endpointItem = (ObjectSyntax)usesItem.Value;
                        var usesService = ((VariableAccessSyntax)endpointItem.SafeGetPropertyByName("app").Value);

                        var path = ((StringSyntax)endpointItem.SafeGetPropertyByName("path")?.Value)?.TryGetLiteralValue();

                        result.Rules.Add(new ConfigIngressRule()
                        {
                            Service = usesService.Name.IdentifierName,
                            Path = path
                        });
                    }

                    yield return result;
                }
            }

        }

        private static IEnumerable<RegisteredApp> GetApps(SemanticModel model)
        {
            foreach (var descendant in model.Root.Descendants)
            {
                if (descendant is ResourceSymbol resourceSymbol)
                {
                    var reference = resourceSymbol.TryGetResourceTypeReference();
                    if (!BoopResourceProvider.IsAppType(reference))
                    {
                        continue;
                    }

                    List<RegisteredAppResourceUsage> uses = new();
                    var body = resourceSymbol.DeclaringResource.GetBody();
                    var project = ((StringSyntax)body.SafeGetPropertyByName("project").Value).TryGetLiteralValue();
                    var deployTo = ((VariableAccessSyntax)body.SafeGetPropertyByName("deployTo")?.Value);

                    ResourceSymbol deployToResource = null;
                    if (deployTo != null)
                    {
                        deployToResource = model.Root.GetDeclarationsByName(deployTo.Name.IdentifierName).Single() as ResourceSymbol;
                    }
                    foreach (var usesItem in ((ArraySyntax)body.SafeGetPropertyByName("uses")?.Value)?.Items ?? Array.Empty<ArrayItemSyntax>())
                    {
                        var usesBody = (ObjectSyntax)usesItem.Value;
                        var usesService = ((VariableAccessSyntax)usesBody.SafeGetPropertyByName("service").Value);
                        var usesResource = model.Root.GetDeclarationsByName(usesService.Name.IdentifierName).Single() as ResourceSymbol;
                        var usesRole = ((StringSyntax)usesBody.SafeGetPropertyByName("role")?.Value)?.TryGetLiteralValue()?.Split(",") ?? Array.Empty<string>();
                        uses.Add(new RegisteredAppResourceUsage(usesResource, usesRole));
                    }

                    var runtimeType = !BoopResourceProvider.SameReference(BoopResourceProvider.NodeJsAppResource.TypeReference, reference) ? AppRuntimeType.Dotnet : AppRuntimeType.NodeJs;

                    yield return new RegisteredApp(
                        deployToResource,
                        descendant.Name,
                        project,
                        runtimeType,
                        BoopResourceProvider.SameReference(BoopResourceProvider.FunctionAppResource.TypeReference, reference),
                        BoopResourceProvider.SameReference(BoopResourceProvider.DockerAppResource.TypeReference, reference),
                        uses.ToArray()
                    );
                }
            }
        }

        private static SemanticModel GetSemanticModel(string input)
        {
            var inputUri = PathHelper.FilePathToFileUrl(input);

            var fileResolver = new FileResolver();
            var workspace = new Workspace();

            var resourceTypeProvider = new BoopResourceProvider(AzResourceTypeProvider.CreateWithAzTypes());
            var moduleDispatcher = new ModuleDispatcher(new DefaultModuleRegistryProvider(fileResolver));

            var sourceFileGrouping = SourceFileGroupingBuilder.Build(fileResolver, moduleDispatcher, workspace, inputUri);
            var compilation = new Compilation(resourceTypeProvider, sourceFileGrouping);
            var model = compilation.GetEntrypointSemanticModel();

            workspace.UpsertSourceFiles(new[] { new BicepFile(inputUri, ImmutableArray<int>.Empty, new NameAdderRewriter(model).Rewrite(sourceFileGrouping.EntryPoint.ProgramSyntax)) });

            sourceFileGrouping = SourceFileGroupingBuilder.Build(fileResolver, moduleDispatcher, workspace, inputUri);
            compilation = new Compilation(resourceTypeProvider, sourceFileGrouping);
            model = compilation.GetEntrypointSemanticModel();

            workspace.UpsertSourceFiles(new[] { new BicepFile(inputUri, ImmutableArray<int>.Empty, new OutputAdderRewriter(model).Rewrite(sourceFileGrouping.EntryPoint.ProgramSyntax)) });

            sourceFileGrouping = SourceFileGroupingBuilder.Build(fileResolver, moduleDispatcher, workspace, inputUri);
            compilation = new Compilation(resourceTypeProvider, sourceFileGrouping);
            model = compilation.GetEntrypointSemanticModel();

            foreach (var (bicepFile, diagnostics) in compilation.GetAllDiagnosticsByBicepFile())
            {
                foreach (var diagnostic in diagnostics)
                {
                    Console.Error.WriteLine($"{bicepFile.FileUri}, {diagnostic.Message}, {string.Join(",", diagnostic.Span)}");
                }
            }

            return model;
        }


        private static string GetInput()
        {
            return Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.bicep").Single();
        }

        private class OutputAdderRewriter : SyntaxRewriteVisitor
        {
            private readonly SemanticModel _semanticModel;

            public OutputAdderRewriter(SemanticModel semanticModel)
            {
                _semanticModel = semanticModel;
            }

            protected override SyntaxBase ReplaceProgramSyntax(ProgramSyntax syntax)
            {
                syntax = (ProgramSyntax)base.ReplaceProgramSyntax(syntax);

                var children = syntax.Children.ToList();

                foreach (var resource in _semanticModel.AllResources)
                {
                    if (resource.Parent != null) continue;

                    var identifierName = resource.Symbol.Name;
                    children.Add(new OutputDeclarationSyntax(
                        Array.Empty<SyntaxBase>(),
                        SyntaxFactory.CreateToken(TokenType.Identifier, "output"),
                        SyntaxFactory.CreateIdentifier("_"+identifierName),
                        new TypeSyntax(SyntaxFactory.CreateToken(TokenType.Identifier, "object")),
                        SyntaxFactory.AssignmentToken,
                        new VariableAccessSyntax(SyntaxFactory.CreateIdentifier(identifierName))
                    ));
                }

                return new ProgramSyntax(children, syntax.EndOfFile, syntax.LexerDiagnostics);
            }
        }

        private class NameAdderRewriter: SyntaxRewriteVisitor
        {
            private readonly SemanticModel _semanticModel;

            public NameAdderRewriter(SemanticModel semanticModel)
            {
                _semanticModel = semanticModel;
            }


            protected override SyntaxBase ReplaceResourceDeclarationSyntax(ResourceDeclarationSyntax syntax)
            {
                var orig = syntax;
                syntax = (ResourceDeclarationSyntax)base.ReplaceResourceDeclarationSyntax(syntax);

                bool needsLocation = false;
                bool needsName = false;

                var symbol =  _semanticModel.Binder.GetSymbolInfo(orig);
                if (symbol is ResourceSymbol { Type: ResourceType { Body: ObjectType objectType}} )
                {
                    needsLocation = objectType.Properties.TryGetValue("location", out var locationProperty) && (locationProperty.Flags & TypePropertyFlags.FallbackProperty) == 0;
                    needsName = objectType.Properties.TryGetValue("name", out var nameProperty) && (nameProperty.Flags & TypePropertyFlags.FallbackProperty) == 0;
                }

                if (syntax.Value is ObjectSyntax objectSyntax)
                {
                    var properties = objectSyntax.Children.ToList();
                    if (needsLocation && !objectSyntax.Properties.Any(p => p.Key is IdentifierSyntax { IdentifierName: "location" }))
                    {
                        properties.Add(
                        SyntaxFactory.CreateObjectProperty("location",
                            new PropertyAccessSyntax(new FunctionCallSyntax(
                                SyntaxFactory.CreateIdentifier("resourceGroup"),
                                SyntaxFactory.LeftParenToken,
                                Array.Empty<FunctionArgumentSyntax>(),
                                SyntaxFactory.RightParenToken), SyntaxFactory.DotToken, SyntaxFactory.CreateIdentifier("location"))));
                    }
                    if (needsName && !objectSyntax.Properties.Any(p => p.Key is IdentifierSyntax { IdentifierName: "name" }))
                    {
                        properties.Add(
                            SyntaxFactory.CreateObjectProperty("name",
                                new StringSyntax(new []
                                {
                                    SyntaxFactory.CreateStringInterpolationToken(true, false, ""),
                                    SyntaxFactory.CreateStringInterpolationToken(false, true, syntax.Name.IdentifierName),
                                },

                                    new [] {
                                        new PropertyAccessSyntax(new FunctionCallSyntax(
                                        SyntaxFactory.CreateIdentifier("resourceGroup"),
                                        SyntaxFactory.LeftParenToken,
                                        Array.Empty<FunctionArgumentSyntax>(),
                                        SyntaxFactory.RightParenToken), SyntaxFactory.DotToken, SyntaxFactory.CreateIdentifier("name")),
                                    },
                                    new string[] { "", syntax.Name.IdentifierName }))) ;
                    }

                    return new ResourceDeclarationSyntax(syntax.LeadingNodes, syntax.Keyword, syntax.Name, syntax.Type, syntax.ExistingKeyword, syntax.Assignment,
                        new ObjectSyntax(objectSyntax.OpenBrace, properties, objectSyntax.CloseBrace)
                    );
                }

                return syntax;
            }
        }
        private static void InitializeThreadPoolSettings(int serviceCount)
        {
            ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);

            // We need to bump up the min threads to something reasonable so that the dashboard doesn't take forever
            // to serve requests. All console IO is blocking in .NET so capturing stdoutput and stderror results in blocking thread pool threads.
            // The thread pool handles bursts poorly and HTTP requests end up getting stuck behind spinning up docker containers and processes.

            // Bumping the min threads doesn't mean we'll have min threads to start, it just means don't add a threads very slowly up to
            // min threads
            ThreadPool.SetMinThreads(Math.Max(workerThreads, serviceCount * 4), completionPortThreads);

            // We use serviceCount * 4 because we currently launch multiple processes per service, this gives the dashboard some breathing room
        }

        private static async Task ExecuteDeployAsync(OutputContext output, ApplicationBuilder application, string environment, bool interactive, bool force)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            if (await KubectlDetector.GetKubernetesServerVersion(output) == null)
            {
                throw new CommandException($"Cannot apply manifests because kubectl is not installed.");
            }

            if (!await KubectlDetector.IsKubectlConnectedToClusterAsync(output))
            {
                throw new CommandException($"Cannot apply manifests because kubectl is not connected to a cluster.");
            }

            ApplyRegistry(output, application, interactive, requireRegistry: true);

            var executor = new ApplicationExecutor(output)
            {
                ServiceSteps =
                {
                    new ApplyContainerDefaultsStep(),
                    new CombineStep() { Environment = environment, },
                    new PublishProjectStep(),
                    new BuildDockerImageStep() { Environment = environment, },
                    new PushDockerImageStep() { Environment = environment, },
                    new ValidateSecretStep() { Environment = environment, Interactive = interactive, Force = force, },
                    new GenerateServiceKubernetesManifestStep() { Environment = environment, },
                },

                IngressSteps =
                {
                    new ValidateIngressStep() { Environment = environment, Interactive = interactive, Force = force, },
                    new GenerateIngressKubernetesManifestStep(),
                },

                ApplicationSteps =
                {
                    new DeployApplicationKubernetesManifestStep(),
                }
            };

            await executor.ExecuteAsync(application);

            watch.Stop();

            TimeSpan elapsedTime = watch.Elapsed;

            output.WriteAlwaysLine($"Time Elapsed: {elapsedTime.Hours:00}:{elapsedTime.Minutes:00}:{elapsedTime.Seconds:00}:{elapsedTime.Milliseconds / 10:00}");
        }

        internal static void ApplyRegistry(OutputContext output, ApplicationBuilder application, bool interactive, bool requireRegistry)
        {
            if (application.Registry is null && interactive)
            {
                var registry = output.Prompt("Enter the Container Registry (ex: 'example.azurecr.io' for Azure or 'example' for dockerhub)", allowEmpty: !requireRegistry);
                if (!string.IsNullOrWhiteSpace(registry))
                {
                    application.Registry = new ContainerRegistry(registry.Trim());
                }
            }
            else if (application.Registry is null && requireRegistry)
            {
                throw new CommandException("A registry is required for deploy operations. Add the registry to 'tye.yaml' or use '-i' for interactive mode.");
            }
            else
            {
                // No registry specified, and that's OK!
            }
        }

        // We have too many options to use the lambda form with each option as a parameter.
        // This is slightly cleaner anyway.
        private class RunCommandArguments
        {
            public IConsole Console { get; set; } = default!;

            public bool Dashboard { get; set; }

            public string[] Debug { get; set; } = Array.Empty<string>();

            public string Dtrace { get; set; } = default!;

            public bool Docker { get; set; }

            public string Logs { get; set; } = default!;

            public string Metrics { get; set; } = default!;

            public bool NoBuild { get; set; }

            public FileInfo Path { get; set; } = default!;

            public int? Port { get; set; }

            public Verbosity Verbosity { get; set; } = Verbosity.Info;

            public bool Watch { get; set; }

            public string Framework { get; set; } = default!;

            public string[] Tags { get; set; } = Array.Empty<string>();
        }
    }

    internal class DeployCommandArguments
    {
        public IConsole Console { get; set; } = default!;

        public FileInfo Path { get; set; } = default!;

        public Verbosity Verbosity { get; set; } = Verbosity.Info;

        public string Namespace { get; set; } = default!;

        public bool Interactive { get; set; } = false;

        public string Framework { get; set; } = default!;

        public bool Force { get; set; } = false;

        public string[] Tags { get; set; } = Array.Empty<string>();
    }
    public class BoopResourceProvider: IResourceTypeProvider
    {
        private readonly IResourceTypeProvider _resourceTypeProviderImplementation;

        public static ObjectType AppBodyObjectType { get; } = new ObjectType(
            "dotnetapp",
            TypeSymbolValidationFlags.Default,
            new[]
            {
                new TypeProperty("project", LanguageConstants.String, TypePropertyFlags.Required),
                new TypeProperty("deployTo", LanguageConstants.ResourceRef),
                new TypeProperty("uses", new TypedArrayType(
                    new ObjectType("serviceReference", TypeSymbolValidationFlags.Default, new[]
                    {
                        new TypeProperty("service", LanguageConstants.ResourceRef, TypePropertyFlags.Required),
                        new TypeProperty("role", LanguageConstants.String),
                    }, null),
                    TypeSymbolValidationFlags.Default
                ))
            },
            null
        );

        public static ObjectType IngressObjectType { get; } = new ObjectType(
            "ingress",
            TypeSymbolValidationFlags.Default,
            new[]
            {
                new TypeProperty("endpoints", new TypedArrayType(
                    new ObjectType("endpoint", TypeSymbolValidationFlags.Default, new[]
                    {
                        new TypeProperty("app", LanguageConstants.ResourceRef, TypePropertyFlags.Required),
                        new TypeProperty("path", LanguageConstants.String),
                    }, null),
                    TypeSymbolValidationFlags.Default
                ))
            },
            null
        );

        public static ResourceType DotNetAppResource { get; } = new(new ResourceTypeReference("Boop", new []{ "dotnetapp" }, "v1"), ResourceScope.Module, AppBodyObjectType);
        public static ResourceType NodeJsAppResource { get; } = new(new ResourceTypeReference("Boop", new[] { "nodejsapp" }, "v1"), ResourceScope.Module, AppBodyObjectType);
        public static ResourceType DockerAppResource { get; } = new(new ResourceTypeReference("Boop", new []{ "dockerapp" }, "v1"), ResourceScope.Module, AppBodyObjectType);
        public static ResourceType FunctionAppResource { get; } = new(new ResourceTypeReference("Boop", new []{ "functionapp" }, "v1"), ResourceScope.Module, AppBodyObjectType);
        public static ResourceType IngressResource { get; } = new(new ResourceTypeReference("Boop", new []{ "ingress" }, "v1"), ResourceScope.Module, IngressObjectType);

        private static ResourceType[] _appTypes = new[]
        {
            DotNetAppResource,
            NodeJsAppResource,
            DockerAppResource,
            FunctionAppResource
        };

        private static ResourceType[] _allTypes = _appTypes.Concat(new [] { IngressResource }).ToArray();

        public BoopResourceProvider(IResourceTypeProvider resourceTypeProviderImplementation)
        {
            _resourceTypeProviderImplementation = resourceTypeProviderImplementation;
        }

        public ResourceType GetType(ResourceTypeReference reference, ResourceTypeGenerationFlags flags)
        {
            return _allTypes.SingleOrDefault(t=>SameReference(reference, t.TypeReference)) ?? _resourceTypeProviderImplementation.GetType(reference, flags);
        }

        public bool HasType(ResourceTypeReference typeReference)
        {
            return _allTypes.Any(t => SameReference(typeReference, t.TypeReference)) || _resourceTypeProviderImplementation.HasType(typeReference);
        }

        public IEnumerable<ResourceTypeReference> GetAvailableTypes()
        {
            return _appTypes.Select(r => r.TypeReference).Concat(_resourceTypeProviderImplementation.GetAvailableTypes());
        }

        public static bool SameReference(ResourceTypeReference reference, ResourceTypeReference t)
        {
            return t.Namespace == reference.Namespace && t.TypesString == reference.TypesString;
        }

        public static bool IsAppType(ResourceTypeReference resourceTypeReference)
        {
            return _appTypes.Any(t => SameReference(resourceTypeReference, t.TypeReference));
        }
    }

    internal record DeployedResource(ResourceSymbol Resource, string Id, string Name, JsonElement Properties);

    internal record RegisteredAppResourceUsage(ResourceSymbol Resource, string[] Roles);

    internal record ResourceIdentity(string principalId);

    internal record AksResource(ResourceIdentity identity, AksResourceIdentityProfile identityProfile);
    internal record AksResourceIdentityProfile(KubeletIdentity kubeletidentity);
    internal record KubeletIdentity(string objectId, string clientId);

    internal enum AppRuntimeType
    {
        Dotnet,
        NodeJs,
    }

    internal record RegisteredApp(ResourceSymbol Resource, string Name, string Path, AppRuntimeType Runtime, bool IsFunction, bool IsDocker, RegisteredAppResourceUsage[] Uses);

    internal record BoopEnvironment(string SubscriptionId, string Name, string UserName);
}
