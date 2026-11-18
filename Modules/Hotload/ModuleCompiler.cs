using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Lokn.Modules.Hotload;

/// <summary>Holds one module source file as read from disk.</summary>
/// <param name="FilePath">The path relative to the module's directory, used in diagnostics and stack traces.</param>
/// <param name="Text">The C# source text.</param>
internal readonly record struct SourceFile( string FilePath, string Text );

/// <summary>Compiles a module's C# sources in memory with Roslyn; <see cref="ModuleCache"/> loads the result.</summary>
internal static class ModuleCompiler
{
	/// <summary>Defines the namespaces imported everywhere, matching the application's implicit usings.</summary>
	private const string ImplicitUsings = """
		global using System;
		global using System.Collections.Generic;
		global using System.IO;
		global using System.Linq;
		global using System.Net.Http;
		global using System.Threading;
		global using System.Threading.Tasks;
		""";

	/// <summary>Stores the language version shared with the application's own projects.</summary>
	private static readonly CSharpParseOptions _parseOptions = new( LanguageVersion.CSharp14 );

	/// <summary>Stores library output settings with nullable analysis enabled like the application.</summary>
	private static readonly CSharpCompilationOptions _compilationOptions = new(
		OutputKind.DynamicallyLinkedLibrary,
		nullableContextOptions: NullableContextOptions.Enable );

	/// <summary>Stores portable symbols so module exceptions report source lines.</summary>
	private static readonly EmitOptions _emitOptions = new( debugInformationFormat: DebugInformationFormat.PortablePdb );

	/// <summary>Stores every assembly the running application can load, built on first compile.</summary>
	private static readonly Lazy<MetadataReference[]> _references = new( BuildReferences );

	/// <summary>Compiles sources into an assembly image and its portable symbols.</summary>
	/// <exception cref="InvalidDataException">The sources do not compile; the message describes the first error.</exception>
	internal static (byte[] Image, byte[] Symbols) Emit( string name, IReadOnlyList<SourceFile> sources )
	{
		var trees = sources
			.Select( source => BuildTree( source.Text, source.FilePath ) )
			.Prepend( BuildTree( ImplicitUsings, "ImplicitUsings.cs" ) );

		var compilation = CSharpCompilation.Create( ModuleCache.BuildAssemblyName( name ), trees, _references.Value, _compilationOptions );
		var image = new MemoryStream();
		var symbols = new MemoryStream();
		var result = compilation.Emit( image, symbols, options: _emitOptions );
		if ( !result.Success )
			throw new InvalidDataException( DescribeErrors( result.Diagnostics ) );

		return (image.ToArray(), symbols.ToArray());
	}

	/// <summary>Parses one file with an encoding, which symbol emission requires.</summary>
	private static SyntaxTree BuildTree( string text, string filePath )
	{
		return CSharpSyntaxTree.ParseText( SourceText.From( text, Encoding.UTF8 ), _parseOptions, filePath );
	}

	/// <summary>Builds references to the framework, Goo, and Lokn assemblies of the running process.</summary>
	private static MetadataReference[] BuildReferences()
	{
		var assemblies = (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )!;
		return assemblies
			.Split( Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries )
			.Select( path => (MetadataReference)MetadataReference.CreateFromFile( path ) )
			.ToArray();
	}

	/// <summary>Describes the first compile error and how many more follow, fitting the shell notice.</summary>
	private static string DescribeErrors( IEnumerable<Diagnostic> diagnostics )
	{
		var errors = diagnostics
			.Where( diagnostic => diagnostic.Severity == DiagnosticSeverity.Error )
			.ToArray();
		if ( errors.Length == 1 )
			return errors[0].ToString();

		return $"{errors[0]} (+{errors.Length - 1} more)";
	}
}
