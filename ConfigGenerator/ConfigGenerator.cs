using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.TypedConstantExtensions;

namespace ConfigGenerator;

[Generator(LanguageNames.CSharp)]
public class ConfigGenerator : IIncrementalGenerator
{
    private abstract class ConfigVariable
    {
        public string Type { get; set; } = null!;
        public string Name { get; set; } = null!;
        public bool Anonymise { get; set; } = false;
        public bool PrintVar { get; set; } = true;
        protected static readonly Regex EnvNameConverterRegex = new("(?<=[a-z])(?=[A-Z])");

        public virtual string GetDefinitionString()
        {
            return $"public static {Type} {Name} {{ get; private set; }}";
        }

        public abstract string GetInitialiserString();
    }

    private abstract class LoadableConfigVariable : ConfigVariable
    {
        public Optional<string> DefaultValue { get; set; } = new();

        public override string GetDefinitionString()
        {
            return
                $"public static {Type} {Name} {{ get; private set; }}{(DefaultValue.HasValue ? $" = {DefaultValue.Value};" : "")}";
        }
    }

    private class EnvConfigVariable : LoadableConfigVariable
    {
        public bool IsJson { get; set; } = false;

        public override string GetInitialiserString()
        {
            return
                $"{Name} = {(IsJson ? "GetEnvVarJson" : "GetEnvVar")}<{Type}>(\"{EnvNameConverterRegex.Replace(Name, "_").ToUpper()}\"{(DefaultValue.HasValue ? $", {DefaultValue.Value}" : "")});";
        }
    }


    private class ConstantConfigVariable : ConfigVariable
    {
        public string ConstantVariable { get; set; } = null!;

        public override string GetInitialiserString()
        {
            return $"{Name} = {ConstantVariable};";
        }
    }

    private class VaultConfigVariable : LoadableConfigVariable
    {
        public string VaultVariable { get; set; } = null!;

        public override string GetInitialiserString()
        {
            var normalizedName = EnvNameConverterRegex.Replace(Name, "_").ToLower();
            return
                $"{Name} = HostNotFound ? string.Empty : LoadFromVault(\"{VaultVariable}\", \"{normalizedName}\"{(DefaultValue.HasValue ? $", {DefaultValue.Value}" : "")});";
        }
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classes = context.SyntaxProvider.ForAttributeWithMetadataName(
            "ConfigGenerator.ConfigMarker",
            predicate: PredicateTest,
            transform: TransformTest
        ).Where(m => m is not null);
        var compilationClasses = context.CompilationProvider.Combine(classes.Collect());
        context.RegisterSourceOutput(compilationClasses, (spc, classDecl) =>
        {
            var (comp, cls) = classDecl;
            foreach (var symbol in cls.Where(v => v is not null))
            {
                GenerateClass(spc, symbol!);
            }
        });
    }

    private static void GenerateClass(SourceProductionContext spc, INamedTypeSymbol symbol)
    {
        var namespaceName = symbol.ContainingNamespace.ToDisplayString();
        var className = symbol.Name;

        var usings = new HashSet<string>
        {
            "using System.Globalization;",
            "using Microsoft.Extensions.Logging;",
            "using Microsoft.Extensions.Logging.Console;",
            "using System.Text.RegularExpressions;",
            "using System.Text.Json;",
            "using System.Net.Http;",
            "using System.Net;"
        };
        var variableSet = new HashSet<string>();
        var variables = new StringBuilder();
        var vaultVariables = new StringBuilder();
        var constructor = new StringBuilder();
        var print = new StringBuilder("Loaded values:\\n");
        var elements = new List<ConfigVariable>();
        foreach (var attribute in symbol.GetAttributes())
        {
            if (string.IsNullOrEmpty(attribute.AttributeClass?.Name) || attribute.AttributeClass is null)
            {
                continue;
            }

            if (attribute.AttributeClass.Name is not
                (
                "ConfigElement"
                or "VaultConfigElement"
                or "ConstantConfigElement"
                or "EnvConfigElement"
                ))
            {
                continue;
            }

            var configVar = ParseConfigElement(attribute, usings);

            variableSet.Add(configVar.Name);
            elements.Add(configVar);
        }

        foreach (var v in elements)
        {
            variables.AppendLine($"    {v.GetDefinitionString()}");

            if (v.PrintVar)
            {
                print.Append(
                    v.Anonymise
                        ? $"       - {v.Name}: {{string.Join(\"\", Enumerable.Repeat('*', {v.Name}.ToString().Length))}}\\n"
                        : $"       - {v.Name}: {{{v.Name}}}\\n");
            }


            constructor.AppendLine($"        {v.GetInitialiserString()};");
        }

        var source = GenerateMainSource(usings, namespaceName, className, variables, constructor, print);
        spc.AddSource($"{className}.g.cs", SourceText.From(source, Encoding.UTF8));

        if (!elements.Any(v => v is VaultConfigVariable))
        {
            return;
        }

        if (!variableSet.Contains("VaultIp"))
        {
            spc.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("CCG1", "Missing vault-ip",
                "A default member named VaultIp has been added to load the vault ip from env-vars", "",
                DiagnosticSeverity.Info, true), null));
            vaultVariables.AppendLine($"    public static string? VaultIp {{ get; }}");
        }

        if (!variableSet.Contains("VaultToken"))
        {
            spc.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("CCG2", "Missing vault-token",
                "A default member named VaultToken has been added to load the token from env-vars", "",
                DiagnosticSeverity.Info, true), null));
            vaultVariables.AppendLine($"    public static string? VaultToken {{ get; }}");
        }

        if (!variableSet.Contains("VaultUnsealKey"))
        {
            spc.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("CCG3", "Missing vault-unseal-key",
                "A default member named VaultUnsealKey has been added to load the unseal key from env-vars", "",
                DiagnosticSeverity.Info, true), null));
            vaultVariables.AppendLine($"    public static string? VaultUnsealKey {{ get; }}");
        }

        var vaultSource = GenerateVaultSource(usings, namespaceName, className);
        spc.AddSource($"{className}.vault.g.cs", SourceText.From(vaultSource, Encoding.UTF8));
    }

    private static ConfigVariable SelectVarType(AttributeData attribute)
    {
        if (attribute.AttributeClass!.Name == "ConstantConfigElement")
        {
            var param = attribute.NamedArguments.FirstOrDefault(v => v.Key == "Init");
            if (param.Value.Value is string value)
            {
                return new ConstantConfigVariable
                {
                    ConstantVariable = value
                };
            }
        }
        else if (attribute.AttributeClass!.Name == "VaultConfigElement" ||
                 attribute.AttributeClass!.Name == "EnvConfigElement")
        {
            var defaultValue = attribute.NamedArguments.FirstOrDefault(v => v.Key == "Default");
            LoadableConfigVariable ret;
            switch (attribute.AttributeClass!.Name)
            {
                case "VaultConfigElement":
                {
                    var param = attribute.NamedArguments.FirstOrDefault(v => v.Key == "FromVaultPath");
                    if (param.Value.Value is string value)
                    {
                        ret = new VaultConfigVariable()
                        {
                            VaultVariable = value
                        };
                    }
                    else
                    {
                        throw new Exception("No vault path specified");
                    }

                    break;
                }
                case "EnvConfigElement":
                {
                    var param = attribute.NamedArguments.FirstOrDefault(v => v.Key == "IsJson");
                    ret = new EnvConfigVariable
                    {
                        IsJson = param.Value.Value as bool? ?? false
                    };

                    break;
                }
                default: throw new InvalidOperationException("Unreachable 2");
            }

            if (!defaultValue.Value.IsNull)
            {
                ret.DefaultValue = new Optional<string>(ToLiteral(defaultValue.Value));
            }

            return ret;
        }

        throw new InvalidOperationException("Unreachable 2");
    }

    private static ConfigVariable ParseConfigElement(AttributeData attribute, HashSet<string> usings)
    {
        var configVar = SelectVarType(attribute);

        foreach (var v in attribute.NamedArguments)
        {
            switch (v.Key)
            {
                case "Type":
                    var type = v.Value.Value as INamedTypeSymbol ??
                               v.Value.Value as ITypeSymbol;
                    var typeUsing = type?.ContainingNamespace?.ToDisplayString();
                    if (typeUsing is not null)
                    {
                        usings.Add($"using {typeUsing};");
                    }

                    configVar.Type = type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    break;
                case "Name":
                    if (v.Value.Value is string name)
                    {
                        configVar.Name = name;
                    }

                    break;
                case "Anonymise":
                    if (v.Value.Value is bool anon)
                    {
                        configVar.Anonymise = anon;
                    }

                    break;
                case "PrintVar":
                    if (v.Value.Value is bool printV)
                    {
                        configVar.PrintVar = printV;
                    }

                    break;
            }
        }

        return configVar;
    }

    private static string GenerateVaultSource(HashSet<string> usings, string namespaceName, string className)
    {
        var vaultSource =
            $$""""
              {{string.Join("\n", usings.OrderBy(v => v))}}
              namespace {{namespaceName}}
              {
              #nullable enable
              public static partial class {{className}}
              {
                  public class VaultException(string message) : Exception(message);
                  private static readonly HttpClient Client = new();
                  private static readonly Dictionary<string, Dictionary<string, string>> VaultCache = new Dictionary<string, Dictionary<string, string>>();
                  private static bool HostNotFound;
                  
                  public static string LoadFromVault(string path, string member, string? defaultValue = null)
                  {
                    if(string.IsNullOrWhiteSpace(VaultIp))
                    {
                        throw new VaultException($"No valid VaultIp has been supplied.");
                    }
                    if(string.IsNullOrWhiteSpace(VaultToken))
                    {
                        throw new VaultException($"No valid VaultToken has been supplied.");
                    }
                    try
                    {
                        return LoadFromVault(path, member, defaultValue, true);
                    }
                    catch (AggregateException e)
                    {
                        HostNotFound = true;
                        return string.Empty;
                    }
                  }
                  
                  private static string LoadFromVault(string path, string member, string? defaultValue, bool firstTime)
                  {
                    if(!VaultCache.TryGetValue(path, out var cached))
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, $"{VaultIp}/v1/data/{path}");
                        request.Headers.Add("X-Vault-Token", VaultToken);
                        var res = Client.SendAsync(request).Result;
                        if(!res.IsSuccessStatusCode)
                        {
                            if(res.StatusCode == HttpStatusCode.ServiceUnavailable && firstTime)
                            {
                                UnsealVault();
                                return LoadFromVault(path, member, defaultValue, false);
                            }
                            throw new VaultException($"Could not load {path}: {res.ReasonPhrase}.");                        
                        }
                        var resString = res.Content.ReadAsStringAsync().Result;
                        cached = JsonSerializer.Deserialize<VaultResponse>(resString).data.data;
                        if(cached is null)
                        {
                            throw new VaultException($"Could not load {path}: JsonSerializer failed.");
                        }
                        VaultCache.Add(path, cached);
                    }
                    try
                    {
                        return cached[member];
                    }
                    catch
                    {
                        if(defaultValue is not null)
                        {
                            return defaultValue;
                        }
                        throw new VaultException($"Could not load {member} from {path}.");
                    }
                  }
                  
                  static void UnsealVault()
                  {
                      if(string.IsNullOrWhiteSpace(VaultUnsealKey))
                      {
                          throw new VaultException($"No valid VaultUnsealKey was found in the vault.");
                      }
                      var request = new HttpRequestMessage(HttpMethod.Post, $"{VaultIp}/v1//sys/unseal");
                      request.Content = new StringContent(JsonSerializer.Serialize(new { key = VaultUnsealKey }));
                      var res = Client.SendAsync(request).Result;
                      if(!res.IsSuccessStatusCode)
                      {
                          throw new VaultException($"Could not unseal vault: {res.ReasonPhrase}.");                        
                      }
                  }
                  
                  public static async Task RenewToken(CancellationToken stoppingToken = default)
                  {
                    if(string.IsNullOrWhiteSpace(VaultIp))
                    {
                        throw new VaultException($"No valid VaultIp has been supplied.");
                    }
                    if(string.IsNullOrWhiteSpace(VaultToken))
                    {
                        throw new VaultException($"No valid VaultToken has been supplied.");
                    }
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{VaultIp}/v1/auth/token/renew-self");
                    request.Headers.Add("X-Vault-Token", VaultToken);
                    await Client.SendAsync(request, stoppingToken);
                  }
                  
                  public class VaultResponse
                  {
                    public string request_id { get; set; } = null!;
                    public string lease_id { get; set; } = null!;
                    public bool renewable { get; set; }
                    public int lease_duration { get; set; }
                    public Data data { get; set; } = null!;
                    public object wrap_info { get; set; } = null!;
                    public object warnings { get; set; } = null!;
                    public object auth { get; set; } = null!;
                    public string mount_type { get; set; } = null!;
                    public class Data
                    {
                        public Dictionary<string, string> data { get; set; }
                        public Metadata metadata { get; set; } = null!;
                    }
                    
                    public class Metadata
                    {
                        public string created_time { get; set; } = null!;
                        public object custom_metadata { get; set; } = null!;
                        public string deletion_time { get; set; } = null!;
                        public bool destroyed { get; set; }
                        public int version { get; set; }
                    }
                  }
              }
              }
              """";
        return vaultSource;
    }

    private static string GenerateMainSource(HashSet<string> usings, string namespaceName, string className,
        StringBuilder variables, StringBuilder constructor, StringBuilder print)
    {
        var source =
            $$""""
              {{string.Join("\n", usings.OrderBy(v => v))}}
              namespace {{namespaceName}}
              {
              #nullable enable
              public static partial class {{className}}
              {
                  public class EnvVarException(string message) : Exception(message);
                  private static readonly ILoggerFactory LoggerFactory;
                  private static readonly ILoggerFactory LoggerFactoryMultiLine;
              {{variables}}
                  static {{className}}()
                  {
                      LoggerFactory =
                          Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
                          {
                              options.SingleLine = true;
                              options.IncludeScopes = true;
                              options.TimestampFormat = "HH:mm:ss ";
                              options.ColorBehavior = LoggerColorBehavior.Enabled;
                          }));
                      LoggerFactoryMultiLine =
                          Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
                          {
                              options.SingleLine = false;
                              options.IncludeScopes = true;
                              options.TimestampFormat = "HH:mm:ss ";
                              options.ColorBehavior = LoggerColorBehavior.Enabled;
                          }));
                      var logger = GetLoggerMulti("Config");
                      Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"), logger, out var i);
              {{constructor}}
                      GetLogger("Config").LogInformation($"Loaded {i} environment variables.");
                      logger.LogInformation($"{{print}}");
                  }
                  /// <summary>
                  /// Returns an env variable or <paramref name="defaultValue"/>
                  /// </summary>
                  /// <param name="name">Name of the variable</param>
                  /// <param name="defaultValue">The (optional) default value</param>
                  /// <returns></returns>
                  /// <exception cref="EnvVarException"></exception>
                  private static string GetEnvVar(string name, string? defaultValue = null)
                  {
                      var res = Environment.GetEnvironmentVariable(name) ?? defaultValue;
                      return res ?? throw new EnvVarException($"Environment variable {name} is not defined and no default value is given.");
                  }

                  /// <inheritdoc cref="GetEnvVar(string, string)"/>
                  private static T GetEnvVar<T>(string name, T? defaultValue = default) where T : IConvertible
                  {
                      var res = Environment.GetEnvironmentVariable(name);
                      if (res is null)
                      {
                          return defaultValue ??
                                 throw new EnvVarException(
                                     $"Environment variable {name} is not defined and no default value is given.");
                      }

                      var parsedResult = Convert.ChangeType(res, typeof(T), CultureInfo.CurrentCulture);
                      return (T)parsedResult;
                  }
                  
                  /// <inheritdoc cref="GetEnvVar(string, string)"/>
                  private static T GetEnvVarJson<T>(string name, T? defaultValue = default) where T : IConvertible
                  {
                      var res = Environment.GetEnvironmentVariable(name);
                      if (res is null)
                      {
                          return defaultValue ??
                                 throw new EnvVarException(
                                     $"Environment variable {name} is not defined and no default value is given.");
                      }
                      var parsed = JsonSerializer.Deserialize<T>(res);
                      if(parsed is null)
                      {
                          throw new EnvVarException($"Could not parse {name}");
                      }
                      return parsed;
                  }
                  
                  /// <inheritdoc cref="GetEnvVar(string, string)"/>
                  private static bool GetEnvVar(string name, bool? defaultValue = null)
                  {
                      var res = Environment.GetEnvironmentVariable(name);
                      if (res is null || !bool.TryParse(res, out var b))
                      {
                          return defaultValue ?? throw new EnvVarException(
                              $"Environment variable {name} is not defined and no default value is given.");
                      }
                  
                      return b;
                  }
                  
                  /// <inheritdoc cref="GetEnvVar(string, string)"/>
                  private static int GetEnvVar(string name, int? defaultValue = null)
                  {
                      var res = Environment.GetEnvironmentVariable(name);
                      if (res is null || !int.TryParse(res, out var i))
                      {
                          return defaultValue ?? throw new EnvVarException(
                              $"Environment variable {name} is not defined and no default value is given.");
                      }
                  
                      return i;
                  }
                  
                  /// <inheritdoc cref="GetEnvVar(string, string)"/>
                  private static double GetEnvVar(string name, double? defaultValue = null)
                  {
                      var res = Environment.GetEnvironmentVariable(name);
                      if (res is null || !double.TryParse(res, out var f))
                      {
                          return defaultValue ?? throw new EnvVarException(
                              $"Environment variable {name} is not defined and no default value is given.");
                      }
                  
                      return f;
                  }
                  
                  /// <inheritdoc cref="GetEnvVar(string, string)"/>
                  private static float GetEnvVar(string name, float? defaultValue = null)
                  {
                      var res = Environment.GetEnvironmentVariable(name);
                      if (res is null || !float.TryParse(res, out var f))
                      {
                          return defaultValue ?? throw new EnvVarException(
                              $"Environment variable {name} is not defined and no default value is given.");
                      }
                  
                      return f;
                  }
                  
                  private static bool Load(string path, ILogger logger, out int n)
                  {
                      n = 0;
                      if (!File.Exists(path))
                      {
                          logger.LogWarning("""
                                             Failed to load .env file
                                             File not found: {Path}
                                             """, path);
                          return false;
                      }
                  
                      var lines = File.ReadAllLines(path).Select(l => CommentMatcher().Replace(l, "").Trim())
                          .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                      foreach (var line in lines)
                      {
                          var split = line.Split('=').Select(l => l.Trim()).ToArray();
                          var name = split[0];
                          var value = split[1];
                          Environment.SetEnvironmentVariable(name, value);
                          n++;
                      }
                  
                      return true;
                  }
                  public static ILogger GetLogger(string name)
                  {
                      return LoggerFactory.CreateLogger(name);
                  }
                  
                  public static ILogger<T> GetLogger<T>()
                  {
                      return LoggerFactory.CreateLogger<T>();
                  }
                  
                  public static ILogger GetLoggerMulti(string name)
                  {
                      return LoggerFactoryMultiLine.CreateLogger(name);
                  }
                  
                  public static ILogger<T> GetLoggerMulti<T>()
                  {
                      return LoggerFactoryMultiLine.CreateLogger<T>();
                  }
              }
              }
              """";
        return source;
    }

    private static bool PredicateTest(SyntaxNode s, CancellationToken token)
    {
        return s is ClassDeclarationSyntax { AttributeLists.Count: > 0 };
    }

    private static INamedTypeSymbol? TransformTest(GeneratorAttributeSyntaxContext context, CancellationToken token)
    {
        var classSyntax = (ClassDeclarationSyntax)context.TargetNode;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classSyntax) as INamedTypeSymbol;
        return symbol;
    }

    private static string ToLiteral(TypedConstant constant)
    {
        if (constant.IsNull) return "null";

        switch (constant.Kind)
        {
            case TypedConstantKind.Primitive:
            {
                var val = constant.Value;
                return val switch
                {
                    bool b => b ? "true" : "false",
                    string s => $"\"{s}\"",
                    char c => $"'{c}'",
                    _ => Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture)!
                };
            }

            case TypedConstantKind.Type:
                var ts = (ITypeSymbol)constant.Value!;
                return $"typeof({ts.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})";

            case TypedConstantKind.Error:
            case TypedConstantKind.Enum:
            case TypedConstantKind.Array:
            default:
                return constant.ToCSharpString();
        }
    }
}