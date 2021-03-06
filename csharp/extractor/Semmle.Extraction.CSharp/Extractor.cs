using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Semmle.Util;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using Semmle.Util.Logging;
using System.Collections.Concurrent;

namespace Semmle.Extraction.CSharp
{
    public static class Extractor
    {
        public enum ExitCode
        {
            Ok,         // Everything worked perfectly
            Errors,     // Trap was generated but there were processing errors
            Failed      // Trap could not be generated
        }

        class LogProgressMonitor : IProgressMonitor
        {
            readonly ILogger Logger;

            public LogProgressMonitor(ILogger logger)
            {
                Logger = logger;
            }

            public void Analysed(int item, int total, string source, string output, TimeSpan time, AnalysisAction action)
            {
                if (action != AnalysisAction.UpToDate)
                {
                    Logger.Log(Severity.Info, "  {0} ({1})", source,
                        action == AnalysisAction.Extracted ? time.ToString() : action == AnalysisAction.Excluded ? "excluded" : "up to date");
                }
            }

            public void MissingNamespace(string @namespace) { }

            public void MissingSummary(int types, int namespaces) { }

            public void MissingType(string type) { }
        }

        /// <summary>
        /// Command-line driver for the extractor.
        /// </summary>
        ///
        /// <remarks>
        /// The extractor can be invoked in one of two ways: Either as an "analyser" passed in via the /a
        /// option to csc.exe, or as a stand-alone executable. In this case, we need to faithfully
        /// drive Roslyn in the way that csc.exe would.
        /// </remarks>
        ///
        /// <param name="args">Command line arguments as passed to csc.exe</param>
        /// <returns><see cref="ExitCode"/></returns>
        public static ExitCode Run(string[] args)
        {
            var commandLineArguments = Options.CreateWithEnvironment(args);
            var fileLogger = new FileLogger(commandLineArguments.Verbosity, GetCSharpLogPath());
            var logger = commandLineArguments.Console
                ? new CombinedLogger(new ConsoleLogger(commandLineArguments.Verbosity), fileLogger)
                : (ILogger)fileLogger;

            if (Environment.GetEnvironmentVariable("SEMMLE_CLRTRACER") == "1" && !commandLineArguments.ClrTracer)
            {
                logger.Log(Severity.Info, "Skipping extraction since already extracted from the CLR tracer");
                return ExitCode.Ok;
            }

            using (var analyser = new Analyser(new LogProgressMonitor(logger), logger))
            using (var references = new BlockingCollection<MetadataReference>())
            {
                try
                {
                    var compilerVersion = new CompilerVersion(commandLineArguments);

                    bool preserveSymlinks = Environment.GetEnvironmentVariable("SEMMLE_PRESERVE_SYMLINKS") == "true";
                    var canonicalPathCache = CanonicalPathCache.Create(logger, 1000, preserveSymlinks ? CanonicalPathCache.Symlinks.Preserve : CanonicalPathCache.Symlinks.Follow);

                    if (compilerVersion.SkipExtraction)
                    {
                        logger.Log(Severity.Warning, "  Unrecognized compiler '{0}' because {1}", compilerVersion.SpecifiedCompiler, compilerVersion.SkipReason);
                        return ExitCode.Ok;
                    }

                    var cwd = Directory.GetCurrentDirectory();
                    var compilerArguments = CSharpCommandLineParser.Default.Parse(
                        compilerVersion.ArgsWithResponse,
                        cwd,
                        compilerVersion.FrameworkPath,
                        compilerVersion.AdditionalReferenceDirectories
                        );

                    if (compilerArguments == null)
                    {
                        var sb = new StringBuilder();
                        sb.Append("  Failed to parse command line: ").AppendList(" ", args);
                        logger.Log(Severity.Error, sb.ToString());
                        ++analyser.CompilationErrors;
                        return ExitCode.Failed;
                    }

                    var referenceTasks = ResolveReferences(compilerArguments, analyser, canonicalPathCache, references);

                    var syntaxTrees = new List<SyntaxTree>();
                    var syntaxTreeTasks = ReadSyntaxTrees(
                        compilerArguments.SourceFiles.
                        Select(src => canonicalPathCache.GetCanonicalPath(src.Path)),
                        analyser,
                        compilerArguments.ParseOptions,
                        compilerArguments.Encoding,
                        syntaxTrees);

                    var sw = new Stopwatch();
                    sw.Start();

                    Parallel.Invoke(
                        new ParallelOptions { MaxDegreeOfParallelism = commandLineArguments.Threads },
                        referenceTasks.Interleave(syntaxTreeTasks).ToArray());

                    if (syntaxTrees.Count == 0)
                    {
                        logger.Log(Severity.Error, "  No source files");
                        ++analyser.CompilationErrors;
                        analyser.LogDiagnostics(compilerVersion.ArgsWithResponse);
                        return ExitCode.Failed;
                    }

                    var compilation = CSharpCompilation.Create(
                        compilerArguments.CompilationName,
                        syntaxTrees,
                        references,
                        compilerArguments.CompilationOptions.
                            WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default).
                            WithStrongNameProvider(new DesktopStrongNameProvider(compilerArguments.KeyFileSearchPaths))
                        // csc.exe (CSharpCompiler.cs) also provides WithMetadataReferenceResolver,
                        // WithXmlReferenceResolver and
                        // WithSourceReferenceResolver.
                        // These would be needed if we hadn't explicitly provided the source/references
                        // already.
                        );

                    analyser.Initialize(compilerArguments, compilation, commandLineArguments, compilerVersion.ArgsWithResponse);
                    analyser.AnalyseReferences();

                    foreach (var tree in compilation.SyntaxTrees)
                    {
                        analyser.AnalyseTree(tree);
                    }

                    sw.Stop();
                    logger.Log(Severity.Info, "  Models constructed in {0}", sw.Elapsed);

                    sw.Restart();
                    analyser.PerformExtraction(commandLineArguments.Threads);
                    sw.Stop();
                    logger.Log(Severity.Info, "  Extraction took {0}", sw.Elapsed);

                    return analyser.TotalErrors == 0 ? ExitCode.Ok : ExitCode.Errors;
                }
                catch (Exception e)
                {
                    logger.Log(Severity.Error, "  Unhandled exception: {0}", e);
                    return ExitCode.Errors;
                }
            }
        }

        /// <summary>
        /// Gets the complete list of locations to locate references.
        /// </summary>
        /// <param name="args">Command line arguments.</param>
        /// <returns>List of directories.</returns>
        static IEnumerable<string> FixedReferencePaths(Microsoft.CodeAnalysis.CommandLineArguments args)
        {
            // See https://msdn.microsoft.com/en-us/library/s5bac5fx.aspx
            // on how csc resolves references. Basically,
            // 1) Current working directory. This is the directory from which the compiler is invoked.
            // 2) The common language runtime system directory.
            // 3) Directories specified by / lib.
            // 4) Directories specified by the LIB environment variable.

            yield return args.BaseDirectory;

            foreach (var r in args.ReferencePaths)
                yield return r;

            var lib = System.Environment.GetEnvironmentVariable("LIB");
            if (lib != null)
                yield return lib;
        }

        static MetadataReference MakeReference(CommandLineReference reference, string path)
        {
            return MetadataReference.CreateFromFile(path).WithProperties(reference.Properties);
        }

        /// <summary>
        /// Construct tasks for resolving references (possibly in parallel).
        ///
        /// The resolved references will be added (thread-safely) to the supplied
        /// list <paramref name="ret"/>.
        /// </summary>
        static IEnumerable<Action> ResolveReferences(Microsoft.CodeAnalysis.CommandLineArguments args, Analyser analyser, CanonicalPathCache canonicalPathCache, BlockingCollection<MetadataReference> ret)
        {
            var referencePaths = new Lazy<string[]>(() => FixedReferencePaths(args).ToArray());
            return args.MetadataReferences.Select<CommandLineReference, Action>(clref => () =>
            {
                if (Path.IsPathRooted(clref.Reference))
                {
                    if (File.Exists(clref.Reference))
                    {
                        var reference = MakeReference(clref, canonicalPathCache.GetCanonicalPath(clref.Reference));
                        ret.Add(reference);
                    }
                    else
                    {
                        lock (analyser)
                        {
                            analyser.Logger.Log(Severity.Error, "  Reference '{0}' does not exist", clref.Reference);
                            ++analyser.CompilationErrors;
                        }
                    }
                }
                else
                {
                    bool referenceFound = false;
                    {
                        foreach (var composed in referencePaths.Value.
                            Select(path => Path.Combine(path, clref.Reference)).
                            Where(path => File.Exists(path)).
                            Select(path => canonicalPathCache.GetCanonicalPath(path)))
                        {
                            referenceFound = true;
                            var reference = MakeReference(clref, composed);
                            ret.Add(reference);
                            break;
                        }
                        if (!referenceFound)
                        {
                            lock (analyser)
                            {
                                analyser.Logger.Log(Severity.Error, "  Unable to resolve reference '{0}'", clref.Reference);
                                ++analyser.CompilationErrors;
                            }
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Construct tasks for reading source code files (possibly in parallel).
        ///
        /// The constructed syntax trees will be added (thread-safely) to the supplied
        /// list <paramref name="ret"/>.
        /// </summary>
        static IEnumerable<Action> ReadSyntaxTrees(IEnumerable<string> sources, Analyser analyser, CSharpParseOptions parseOptions, Encoding encoding, IList<SyntaxTree> ret)
        {
            return sources.Select<string, Action>(path => () =>
            {
                try
                {
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var st = CSharpSyntaxTree.ParseText(SourceText.From(file, encoding), parseOptions, path);
                        lock (ret)
                            ret.Add(st);
                    }
                }
                catch (IOException ex)
                {
                    lock (analyser)
                    {
                        analyser.Logger.Log(Severity.Error, "  Unable to open source file {0}: {1}", path, ex.Message);
                        ++analyser.CompilationErrors;
                    }
                }
            });
        }

        public static void ExtractStandalone(
            IEnumerable<string> sources,
            IEnumerable<string> referencePaths,
            IProgressMonitor pm,
            ILogger logger,
            CommonOptions options)
        {
            using (var analyser = new Analyser(pm, logger))
            using (var references = new BlockingCollection<MetadataReference>())
            {
                try
                {
                    var referenceTasks = referencePaths.Select<string, Action>(path => () =>
                    {
                        var reference = MetadataReference.CreateFromFile(path);
                        references.Add(reference);
                    });

                    var syntaxTrees = new List<SyntaxTree>();
                    var syntaxTreeTasks = ReadSyntaxTrees(sources, analyser, null, null, syntaxTrees);

                    var sw = new Stopwatch();
                    sw.Start();

                    Parallel.Invoke(
                        new ParallelOptions { MaxDegreeOfParallelism = options.Threads },
                        referenceTasks.Interleave(syntaxTreeTasks).ToArray());

                    if (syntaxTrees.Count == 0)
                    {
                        analyser.Logger.Log(Severity.Error, "  No source files");
                        ++analyser.CompilationErrors;
                    }

                    var compilation = CSharpCompilation.Create(
                        "csharp.dll",
                        syntaxTrees,
                        references
                        );

                    analyser.InitializeStandalone(compilation, options);
                    analyser.AnalyseReferences();

                    foreach (var tree in compilation.SyntaxTrees)
                    {
                        analyser.AnalyseTree(tree);
                    }

                    sw.Stop();
                    analyser.Logger.Log(Severity.Info, "  Models constructed in {0}", sw.Elapsed);

                    sw.Restart();
                    analyser.PerformExtraction(options.Threads);
                    sw.Stop();
                    analyser.Logger.Log(Severity.Info, "  Extraction took {0}", sw.Elapsed);

                    foreach (var type in analyser.MissingNamespaces)
                    {
                        pm.MissingNamespace(type);
                    }

                    foreach (var type in analyser.MissingTypes)
                    {
                        pm.MissingType(type);
                    }

                    pm.MissingSummary(analyser.MissingTypes.Count(), analyser.MissingNamespaces.Count());
                }
                catch (Exception e)
                {
                    analyser.Logger.Log(Severity.Error, "  Unhandled exception: {0}", e);
                }
            }
        }

        /// <summary>
        /// Gets the path to the `csharp.log` file written to by the C# extractor.
        /// </summary>
        public static string GetCSharpLogPath() =>
            Path.Combine(GetCSharpLogDirectory(), "csharp.log");

        public static string GetCSharpLogDirectory()
        {
            string snapshot = Environment.GetEnvironmentVariable("ODASA_SNAPSHOT");
            string buildErrorDir = Environment.GetEnvironmentVariable("ODASA_BUILD_ERROR_DIR");
            string traps = Environment.GetEnvironmentVariable("TRAP_FOLDER");
            if (!string.IsNullOrEmpty(snapshot))
            {
                return Path.Combine(snapshot, "log");
            }
            if (!string.IsNullOrEmpty(buildErrorDir))
            {
                // Used by `qltest`
                return buildErrorDir;
            }
            if (!string.IsNullOrEmpty(traps))
            {
                return traps;
            }
            return Directory.GetCurrentDirectory();
        }
    }
}
