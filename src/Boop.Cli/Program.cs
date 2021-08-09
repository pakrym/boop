using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bicep.Core.Emit;
using Bicep.Core.FileSystem;
using Bicep.Core.Parsing;
using Bicep.Core.Registry;
using Bicep.Core.Semantics;
using Bicep.Core.Syntax;
using Bicep.Core.TypeSystem;
using Bicep.Core.TypeSystem.Az;
using Bicep.Core.Workspaces;

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
                        AssignIdentity(resourceMap, app, env.UserName);
                        SetProjectSettings(app, settings);
                    }
                })
            };

            var deployCommand = new Command("deploy")
            {
            };

            deployCommand.Handler = CommandHandler.Create(() =>
            {
                var input = GetInput();
                var env = EnsureEnvironment();
                var model = DeployResources(env, input, out var resourceMap);

                foreach (var app in GetApps(model))
                {
                    var settings = CollectSettings(app, resourceMap);
                    DeployApp(env, app, resourceMap, settings);
                }
            });

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
                deployCommand
            };

            return await rootCommand.InvokeAsync(args);
        }

        private static SemanticModel DeployResources(BoopEnvironment env, string input, out List<DeployedResource> resourceMap)
        {
            Console.WriteLine($"Deploying to subscription {env.SubscriptionId}, resource group {env.Name}");

            if (AzCli.Run<bool>($"group exists -n {env.Name} --subscription {env.SubscriptionId}") == false)
            {
                AzCli.Run<object>($"group create -n {env.Name} --subscription {env.SubscriptionId} --location westus2");
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
                var prop = deploymentResult.GetProperty("properties").GetProperty("outputs").GetProperty("_" + resourceMetadata.Symbol.Name).GetProperty("value");
                var id = prop.GetProperty("resourceId").GetString();
                var sid = prop.GetProperty("subscriptionId").GetString();
                var rg = prop.GetProperty("resourceGroupName").GetString();

                var fullId = $"/subscriptions/{sid}/resourceGroups/{rg}/providers/{id}";
                var name = id.Split("/").Last();

                yield return new DeployedResource(resourceMetadata.Symbol, fullId, name, prop);
            }
        }

        private static void DeployApp(BoopEnvironment env, RegisteredApp app, IEnumerable<DeployedResource> deployedResources, Dictionary<string, string> settings)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            if (new ProcessStartInfo("dotnet", $"publish -c Release {app.Path} -o {tempDir}").ExecuteAndCaptureOutput(out var stdOut, out var stdErr) != 0)
            {
                Console.Error.WriteLine("Failed to publish app." + stdOut + stdErr);
                Environment.Exit(0);
            }

            var destinationArchiveFileName = Path.GetTempFileName() + ".zip";
            ZipFile.CreateFromDirectory(tempDir, destinationArchiveFileName);

            var deployedResource = deployedResources.Single(r => r.Resource.Name == app.Resource.Name);

            AzCli.Run<object>($"webapp config appsettings set --resource-group {env.Name} --name {deployedResource.Name} --subscription {env.SubscriptionId} --settings WEBSITE_RUN_FROM_PACKAGE=1");
            AzCli.Run<object>($"webapp deployment source config-zip --resource-group {env.Name} --name {deployedResource.Name} --subscription {env.SubscriptionId} --src {destinationArchiveFileName}");

            AssignRolesAndSettings(env, deployedResources, app, deployedResource, settings);

            Console.WriteLine($"{app.Path} published to {deployedResource.Id} view at http://{deployedResource.Properties.GetProperty("properties").GetProperty("hostNames")[0]}");
        }

        private static void SetProjectSettings(RegisteredApp app, Dictionary<string,string> settings)
        {
            Dotnet.Run("user-secrets init", app.Path);
            foreach (var setting in settings)
            {
                Dotnet.Run($"user-secrets set {setting.Key} {setting.Value}", app.Path);
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
            var res = AzCli.Run<WebAppIdentity>($"webapp identity assign --resource-group {env.Name} --name {appResource.Name} --subscription {env.SubscriptionId}");

            AssignIdentity(deployedResources, app, res.principalId);

            foreach (var setting in settings)
            {
                var key = setting.Key.Replace(":", "__");
                AzCli.Run<object>($"webapp config appsettings set --resource-group {env.Name} --name {appResource.Name} --subscription {env.SubscriptionId} --settings {key}={setting.Value}");
            }

        }

        private static void AssignIdentity(IEnumerable<DeployedResource> deployedResources, RegisteredApp app, string identity)
        {
            foreach (var usage in app.Uses)
            {
                var deployedResource = deployedResources.Single(r => r.Resource.Name == usage.Resource.Name);

                foreach (var role in usage.Roles)
                {
                    AzCli.Run<object>($"role assignment create --assignee {identity} --role \"{role}\" --scope {deployedResource.Id}");
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

        private static IEnumerable<RegisteredApp> GetApps(SemanticModel model)
        {
            foreach (var descendant in model.Root.Descendants)
            {
                if (descendant is ResourceSymbol resourceSymbol)
                {
                    ResourceSymbol appResource = null;
                    string path = null;
                    List<RegisteredAppResourceUsage> uses = new();

                    foreach (var decoratorSyntax in (resourceSymbol.DeclaringSyntax as ResourceDeclarationSyntax).Decorators)
                    {
                        if (decoratorSyntax.Expression is FunctionCallSyntax { Name: { IdentifierName: "app" } })
                        {
                            appResource = resourceSymbol;
                            path = (decoratorSyntax.Arguments.Single().Expression as StringSyntax).TryGetLiteralValue();
                        }
                        if (decoratorSyntax.Expression is FunctionCallSyntax { Name: { IdentifierName: "uses" } })
                        {
                            var arguments = decoratorSyntax.Arguments.ToList();
                            var name  = (arguments[0].Expression as VariableAccessSyntax).Name.IdentifierName;
                            string[] roles = null;
                            if (arguments.Count > 1)
                            {
                                roles = (arguments[1].Expression as StringSyntax).TryGetLiteralValue().Split(",");
                            }
                            uses.Add(new RegisteredAppResourceUsage(model.Root.GetDeclarationsByName(name).Single() as ResourceSymbol, roles));
                        }
                    }

                    if (appResource != null && path != null)
                    {
                        var appPath = Path.Combine(
                            Path.GetDirectoryName(model.Root.FileUri.AbsolutePath),
                            path);
                        yield return new RegisteredApp(appResource, appPath, uses.ToArray());
                    }
                }
            }
        }

        private static SemanticModel GetSemanticModel(string input)
        {
            var inputUri = PathHelper.FilePathToFileUrl(input);

            var fileResolver = new FileResolver();
            var workspace = new Workspace();

            var resourceTypeProvider = AzResourceTypeProvider.CreateWithAzTypes();
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
                    Console.Error.WriteLine($"{bicepFile.FileUri}, {diagnostic.Message}, {bicepFile.LineStarts}");
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
                bool needsLocation = false;
                bool needsName = false;

                var symbol =  _semanticModel.Binder.GetSymbolInfo(syntax);
                if (symbol is ResourceSymbol { Type: ResourceType { Body: ObjectType objectType}} )
                {
                    needsLocation = objectType.Properties.ContainsKey("location");
                    needsName = objectType.Properties.ContainsKey("name");
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

                return base.ReplaceResourceDeclarationSyntax(syntax);
            }
        }
    }

    internal record DeployedResource(ResourceSymbol Resource, string Id, string Name, JsonElement Properties);

    internal record RegisteredAppResourceUsage(ResourceSymbol Resource, string[] Roles);

    internal record WebAppIdentity(string principalId);

    internal record RegisteredApp(ResourceSymbol Resource, string Path, RegisteredAppResourceUsage[] Uses);

    internal record BoopEnvironment(string SubscriptionId, string Name, string UserName);
}
