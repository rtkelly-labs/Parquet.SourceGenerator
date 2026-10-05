#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051, IL3000, IL2026, IL3050

#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// -----------------------------------------------------------------------------
// ExportApiDocs.cs
//
// Extracts structured, version-baselined API models for public types in
// Parquet.SourceGenerator and Parquet.SourceGenerator.Attributes.
//
// Produces:
//   <out>/api-model.json
//
// Usage:
//   dotnet run scripts/ExportApiDocs.cs
//   dotnet run scripts/ExportApiDocs.cs -- --repo <dir> --out <dir> --version <semver>
// -----------------------------------------------------------------------------

string? repoArg = null;
string? outArg = null;
string? versionArg = null;

string[] argv = Environment.GetCommandLineArgs();
for (int i = 1; i < argv.Length; i++)
{
    if (argv[i] == "--repo" && i + 1 < argv.Length)
        repoArg = argv[++i];
    else if (argv[i] == "--out" && i + 1 < argv.Length)
        outArg = argv[++i];
    else if (argv[i] == "--version" && i + 1 < argv.Length)
        versionArg = argv[++i];
}

string repoRoot = Path.GetFullPath(repoArg ?? Directory.GetCurrentDirectory());
string outputDir = Path.GetFullPath(outArg ?? Path.Combine(repoRoot, "artifacts"));
Directory.CreateDirectory(outputDir);

string version =
    versionArg
    ?? Environment.GetEnvironmentVariable("VERSION")
    ?? ResolveRepoVersion(repoRoot)
    ?? "0.0.1";

var targetProjects = new[]
{
    new
    {
        AssemblyName = "Parquet.SourceGenerator.Attributes",
        RelativePath = "src/Parquet.SourceGenerator.Attributes",
    },
    new { AssemblyName = "Parquet.SourceGenerator", RelativePath = "src/Parquet.SourceGenerator" },
};

var allNamespaces = new Dictionary<string, List<TypeDocModel>>(StringComparer.Ordinal);

var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
var systemRuntime = MetadataReference.CreateFromFile(
    Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")
);

foreach (var proj in targetProjects)
{
    string projDir = Path.Combine(repoRoot, proj.RelativePath);
    if (!Directory.Exists(projDir))
        continue;

    var csFiles = Directory
        .GetFiles(projDir, "*.cs", SearchOption.AllDirectories)
        .Where(f =>
            !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
        )
        .ToList();

    var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Parse);
    var syntaxTrees = new List<SyntaxTree>();
    foreach (var file in csFiles)
    {
        string text = File.ReadAllText(file);
        syntaxTrees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file));
    }

    var compilation = CSharpCompilation.Create(
        proj.AssemblyName,
        syntaxTrees,
        new[] { mscorlib, systemRuntime },
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    foreach (var tree in syntaxTrees)
    {
        var semanticModel = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var typeDeclarations = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>();
        foreach (var typeDecl in typeDeclarations)
        {
            var symbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
            if (symbol == null || !IsPubliclyAccessible(symbol))
                continue;

            string ns = symbol.ContainingNamespace?.ToDisplayString() ?? "Global";
            if (!allNamespaces.TryGetValue(ns, out var typeList))
            {
                typeList = new List<TypeDocModel>();
                allNamespaces[ns] = typeList;
            }

            var typeModel = ExtractType(symbol, proj.AssemblyName);
            // Replace or add
            typeList.RemoveAll(t => t.Id == typeModel.Id);
            typeList.Add(typeModel);
        }
    }
}

var apiDoc = new ApiDocProjectModel
{
    ProjectName = "Parquet.SourceGenerator",
    Version = version,
    GeneratedAt = DateTime.UtcNow.ToString("o"),
    Namespaces = allNamespaces
        .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
        .Select(kvp => new NamespaceDocModel
        {
            Name = kvp.Key,
            Types = kvp.Value.OrderBy(t => t.Name, StringComparer.Ordinal).ToList(),
        })
        .ToList(),
};

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
};

string outFile = Path.Combine(outputDir, "api-model.json");
File.WriteAllText(outFile, JsonSerializer.Serialize(apiDoc, jsonOptions));

Console.WriteLine($"✓ Wrote API model to: {outFile}");
Console.WriteLine($"  Namespaces: {apiDoc.Namespaces.Count}");
Console.WriteLine($"  Total Types: {apiDoc.Namespaces.Sum(n => n.Types.Count)}");

return 0;

static TypeDocModel ExtractType(INamedTypeSymbol symbol, string assemblyName)
{
    var hierarchy = new List<string>();
    var current = symbol.BaseType;
    while (current != null)
    {
        hierarchy.Insert(0, current.ToDisplayString());
        current = current.BaseType;
    }
    hierarchy.Add(symbol.ToDisplayString());

    var typeSyntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
    var (summary, remarks, _) = ExtractDocFromSyntax(typeSyntax);

    string kind = symbol.TypeKind switch
    {
        TypeKind.Class => "Class",
        TypeKind.Struct => "Struct",
        TypeKind.Interface => "Interface",
        TypeKind.Enum => "Enum",
        TypeKind.Delegate => "Delegate",
        _ => symbol.TypeKind.ToString(),
    };

    var constructors = new List<MemberDocModel>();
    var properties = new List<MemberDocModel>();
    var methods = new List<MemberDocModel>();
    var fields = new List<MemberDocModel>();

    foreach (
        var member in symbol
            .GetMembers()
            .Where(m => m.DeclaredAccessibility == Accessibility.Public)
    )
    {
        if (member.IsImplicitlyDeclared)
        {
            // Preserve implicit public parameterless constructors for classes/structs
            if (
                member
                is not IMethodSymbol { MethodKind: MethodKind.Constructor, Parameters.Length: 0 }
            )
                continue;
        }

        var memberSyntax = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        var (memSummary, memRemarks, memParams) = ExtractDocFromSyntax(memberSyntax);

        if (member is IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.Constructor)
            {
                constructors.Add(
                    new MemberDocModel
                    {
                        Name = symbol.Name,
                        Kind = "Constructor",
                        Syntax = method.ToDisplayString(
                            SymbolDisplayFormat.MinimallyQualifiedFormat
                        ),
                        Summary = memSummary,
                        Remarks = memRemarks,
                        Parameters = method
                            .Parameters.Select(p => new ParameterDocModel
                            {
                                Name = p.Name,
                                Type = p.Type.ToDisplayString(),
                                Summary = memParams.TryGetValue(p.Name, out var pSum) ? pSum : null,
                            })
                            .ToList(),
                    }
                );
            }
            else if (
                method.MethodKind
                is MethodKind.Ordinary
                    or MethodKind.UserDefinedOperator
                    or MethodKind.Conversion
            )
            {
                string memberKind = method.MethodKind switch
                {
                    MethodKind.UserDefinedOperator or MethodKind.Conversion => "Operator",
                    _ => "Method",
                };
                methods.Add(
                    new MemberDocModel
                    {
                        Name = method.Name,
                        Kind = memberKind,
                        ReturnType = method.ReturnType.ToDisplayString(),
                        IsStatic = method.IsStatic,
                        Syntax = method.ToDisplayString(
                            SymbolDisplayFormat.MinimallyQualifiedFormat
                        ),
                        Summary = memSummary,
                        Remarks = memRemarks,
                        Parameters = method
                            .Parameters.Select(p => new ParameterDocModel
                            {
                                Name = p.Name,
                                Type = p.Type.ToDisplayString(),
                                Summary = memParams.TryGetValue(p.Name, out var pSum) ? pSum : null,
                            })
                            .ToList(),
                    }
                );
            }
        }
        else if (member is IPropertySymbol prop)
        {
            properties.Add(
                new MemberDocModel
                {
                    Name = prop.Name,
                    Kind = "Property",
                    ReturnType = prop.Type.ToDisplayString(),
                    IsStatic = prop.IsStatic,
                    Syntax = prop.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Summary = memSummary,
                    Remarks = memRemarks,
                }
            );
        }
        else if (member is IFieldSymbol field)
        {
            fields.Add(
                new MemberDocModel
                {
                    Name = field.Name,
                    Kind = "Field",
                    ReturnType = field.Type.ToDisplayString(),
                    IsStatic = field.IsStatic,
                    Syntax = field.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Summary = memSummary,
                    Remarks = memRemarks,
                    ConstantValue = field.ConstantValue?.ToString(),
                }
            );
        }
    }

    return new TypeDocModel
    {
        Id = symbol.ToDisplayString(),
        Name = symbol.Name,
        Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? "Global",
        Assembly = assemblyName,
        Kind = kind,
        IsStatic = symbol.IsStatic,
        IsSealed = symbol.IsSealed,
        IsAbstract = symbol.IsAbstract,
        Summary = summary,
        Remarks = remarks,
        Syntax = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        InheritanceHierarchy = hierarchy,
        Interfaces = symbol
            .AllInterfaces.Select(i => i.ToDisplayString())
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList(),
        Constructors = constructors.OrderBy(c => c.Syntax, StringComparer.Ordinal).ToList(),
        Properties = properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(),
        Methods = methods.OrderBy(m => m.Name, StringComparer.Ordinal).ToList(),
        Fields = fields.OrderBy(f => f.Name, StringComparer.Ordinal).ToList(),
    };
}

static (string? Summary, string? Remarks, Dictionary<string, string> Params) ExtractDocFromSyntax(
    SyntaxNode? node
)
{
    var paramDict = new Dictionary<string, string>(StringComparer.Ordinal);
    if (node == null)
        return (null, null, paramDict);

    var docTrivia = node.GetLeadingTrivia()
        .Select(t => t.GetStructure())
        .OfType<DocumentationCommentTriviaSyntax>()
        .FirstOrDefault();

    if (docTrivia == null)
        return (null, null, paramDict);

    string? summary = null;
    string? remarks = null;

    foreach (var nodeChild in docTrivia.ChildNodes())
    {
        if (nodeChild is XmlElementSyntax element)
        {
            string tagName = element.StartTag.Name.ToString().Trim();
            if (string.Equals(tagName, "summary", StringComparison.OrdinalIgnoreCase))
            {
                summary = CleanXmlContent(element.Content.ToString());
            }
            else if (string.Equals(tagName, "remarks", StringComparison.OrdinalIgnoreCase))
            {
                remarks = CleanXmlContent(element.Content.ToString());
            }
            else if (string.Equals(tagName, "param", StringComparison.OrdinalIgnoreCase))
            {
                var nameAttr = element
                    .StartTag.Attributes.OfType<XmlNameAttributeSyntax>()
                    .FirstOrDefault();
                string? pName = nameAttr?.Identifier.Identifier.ValueText;
                if (!string.IsNullOrEmpty(pName))
                {
                    paramDict[pName] = CleanXmlContent(element.Content.ToString());
                }
            }
        }
    }

    return (summary, remarks, paramDict);
}

static string CleanXmlContent(string raw)
{
    var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim().TrimStart('/', '*').Trim())
        .Where(l => !string.IsNullOrEmpty(l));
    return string.Join(" ", lines);
}

static bool IsPubliclyAccessible(INamedTypeSymbol symbol)
{
    for (INamedTypeSymbol? current = symbol; current != null; current = current.ContainingType)
    {
        if (current.DeclaredAccessibility != Accessibility.Public)
            return false;
    }
    return true;
}

static string? ResolveRepoVersion(string repoRoot)
{
    string propsFile = Path.Combine(repoRoot, "Directory.Build.props");
    if (!File.Exists(propsFile))
        return null;

    string text = File.ReadAllText(propsFile);
    var match = System.Text.RegularExpressions.Regex.Match(text, @"<Version>([^<]+)</Version>");
    return match.Success ? match.Groups[1].Value.Trim() : null;
}

public class ApiDocProjectModel
{
    public required string ProjectName { get; set; }
    public required string Version { get; set; }
    public required string GeneratedAt { get; set; }
    public required List<NamespaceDocModel> Namespaces { get; set; }
}

public class NamespaceDocModel
{
    public required string Name { get; set; }
    public required List<TypeDocModel> Types { get; set; }
}

public class TypeDocModel
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Namespace { get; set; }
    public required string Assembly { get; set; }
    public required string Kind { get; set; }
    public bool IsStatic { get; set; }
    public bool IsSealed { get; set; }
    public bool IsAbstract { get; set; }
    public string? Summary { get; set; }
    public string? Remarks { get; set; }
    public required string Syntax { get; set; }
    public required List<string> InheritanceHierarchy { get; set; }
    public required List<string> Interfaces { get; set; }
    public required List<MemberDocModel> Constructors { get; set; }
    public required List<MemberDocModel> Properties { get; set; }
    public required List<MemberDocModel> Methods { get; set; }
    public required List<MemberDocModel> Fields { get; set; }
}

public class MemberDocModel
{
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public string? ReturnType { get; set; }
    public bool IsStatic { get; set; }
    public required string Syntax { get; set; }
    public string? Summary { get; set; }
    public string? Remarks { get; set; }
    public string? ConstantValue { get; set; }
    public List<ParameterDocModel>? Parameters { get; set; }
}

public class ParameterDocModel
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string? Summary { get; set; }
}
