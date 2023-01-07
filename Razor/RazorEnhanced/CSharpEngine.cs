using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Scripting;
using Microsoft.CodeDom.Providers.DotNetCompilerPlatform;
using System.Linq;
using System.Web.UI;

namespace RazorEnhanced
{
    class CSharpEngine
    {
        private static CSharpEngine m_instance = null;

        public static CSharpEngine Instance
        {
            get
            {
                if (m_instance == null)
                {
                    m_instance = new CSharpEngine();
                }
                return m_instance;
            }
        }
        private CSharpEngine()
        {

        }

        private CompilerParameters CompilerSettings(bool IncludeDebugInformation)
        {
            CompilerParameters parameters = new CompilerParameters();
            List<string> assemblies = GetReferenceAssemblies();
            foreach (string assembly in assemblies)
            {
                parameters.ReferencedAssemblies.Add(assembly);
            }

            parameters.GenerateInMemory = true; // True - memory generation, false - external file generation
            parameters.GenerateExecutable = false; // True - exe file generation, false - dll file generation
            parameters.TreatWarningsAsErrors = false; // Set whether to treat all warnings as errors.
            parameters.WarningLevel = 4; // https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/errors-warnings
            //parameters.CompilerOptions = "/optimize"; // Set compiler argument to optimize output.
            //parameters.CompilerOptions = "-langversion:8.0";
            parameters.CompilerOptions = "-parallel";
            parameters.IncludeDebugInformation = IncludeDebugInformation; // Build in debug or release
            return parameters;
        }
        class CompilerOptions : IProviderOptions
        {
            string _compilerVersion = "8.0";
            IDictionary<string, string> _compilerOptions = new Dictionary<string, string>() { };
            public string CompilerVersion { get => _compilerVersion; set { _compilerVersion = value; } }
            public bool WarnAsError => false;
            public bool UseAspNetSettings => true;
            public string CompilerFullPath => Path.Combine(Assistant.Engine.RootPath, "roslyn", "csc.exe");
            public int CompilerServerTimeToLive => 60 * 60; // 1h
            IDictionary<string, string> IProviderOptions.AllOptions { get => _compilerOptions; }
            IDictionary<string, string> Options { set { _compilerOptions = value; } } // For Debug
        }

        private List<string> GetReferenceAssemblies()
        {
            List<string> list = new();

            string assemblies_cfg_path = Path.Combine(Assistant.Engine.RootPath, "Scripts", "Assemblies.cfg");

            if (File.Exists(assemblies_cfg_path))
            {
                using StreamReader ip = new(assemblies_cfg_path);
                string line;

                while ((line = ip.ReadLine()) != null)
                {
                    if (line.Length > 0 && !line.StartsWith("#"))  // # means comment
                        list.Add(line);
                }
            }

            // Replace with full path all assemblis that are in razor path
            for (int i = 0; i < list.Count; i++)
            {
                string assembly_path = Path.Combine(Assistant.Engine.RootPath, list[i]);
                if (File.Exists(assembly_path))
                {
                    list[i] = assembly_path;
                }
            }

            // Adding Razor and Ultima.dll as default
            list.Add(Assistant.Engine.RootPath + Path.DirectorySeparatorChar + "RazorEnhanced.exe");
            list.Add(Assistant.Engine.RootPath + Path.DirectorySeparatorChar + "Ultima.dll");
            return list;
        }

        private bool ManageCompileResult(CompilerResults results, List<string> errors)
        {
            bool has_error = true;

            if (results.Errors.HasErrors)
            {
                foreach (var error in results.Errors.OfType<CompilerError>().Where(x => !x.IsWarning))
                {
                    errors.Add($"Error ({error.ErrorNumber}) in {error.FileName} at line {error.Line}: {error.ErrorText}");
                }
            }
            else
                has_error = false;

            if (results.Errors.HasWarnings)
            {
                foreach (var warning in results.Errors.OfType<CompilerError>().Where(x => x.IsWarning))
                {
                    errors.Add($"Warning ({warning.ErrorNumber}) in {warning.FileName} at line {warning.Line}: {warning.ErrorText}");
                }
            }
            return has_error;
        }

        /// <summary>
        /// This function search for our custom directive //#import that allows import classes from other C# files
        /// The directive must be added anywhere before the namespace and can be used in C stile with &gt; &lt; or ""
        /// Using relative path with &gt; &lt; the base directory will the Scripts folder
        /// </summary>
        /// <param name="sourceFile">Full path of the source file</param>
        /// <param name="filesList">List of all files that must be compiled (it's a recursive list)</param>
        /// <param name="errors">List of error and warnings</param>
        private static void FindAllIncludedCSharpScript(string sourceFile, HashSet<string> filesList, List<string> errors)
        {
            const string directive = "//#import";

            if (!File.Exists(sourceFile))
            {
                errors.Add($"Error on directive {directive}. Unable to find {sourceFile}");
                return;
            }

            string basePath = Path.GetDirectoryName(sourceFile); // BasePath of the imported file
            filesList.Add(sourceFile);

            // Searching all the lines with the directive
            var lineNumber = 0;
            foreach (string line in File.ReadAllLines(sourceFile))
            {
                lineNumber++;
                // If namespace directive is found stop searching
                if (line.ToLower().Contains("namespace")) 
                    break;
                if (line.Contains(directive))
                {
                    var import = line.Replace(directive, "").Trim();
                    var file = ExtractFile(import, basePath, directive, lineNumber, errors);

                    if (!filesList.Contains(file))
                        FindAllIncludedCSharpScript(file, filesList, errors);
                }
            }

            // If nothing is found return only the main file
        }

        private static string ExtractFile(string import, string basePath, string directive, int lineNumber, List<string> errors)
        {
            if (import.StartsWith("<") && import.EndsWith(">"))
            {
                // Relative path. Adding base folder
                var file = import.Substring(1, import.Length - 2); // Removes < >
                return Path.GetFullPath(Path.Combine(basePath, file)); // Basepath is Scripts folder
            }
            else if (import.StartsWith("\"") && import.EndsWith("\""))
            {
                // Absolute path. Adding as is
                var file = import.Substring(1, import.Length - 2); // Removes " "
                return Path.GetFullPath(file); // This should resolve the relative ../ path
            }
            errors.Add($"Error on RE Directive {directive} at line {lineNumber}");

            return null;
        }

        /// <summary>
        /// This function checks for directive //#forcerelease
        /// If this directive is present, script will be builded in release instead of debug
        /// </summary>
        /// <param name="sourceFile">Filename of the main source file</param>
        /// <returns></returns>
        private bool CheckForceReleaseDirective(string sourceFile)
        {
            const string directive = "//#forcerelease";

            // Searching the directive in all lines untill "namespace"
            foreach (string line in File.ReadAllLines(sourceFile))
            {
                if (line.ToLower().Contains(directive))
                {
                    return true;
                }

                // If namespace directive is found stop searching
                if (line.ToLower().Contains("namespace")) { break; }
            }
            return false;
        }


        /*
        public bool CompileFromText(string source, out List<string> errorwarnings, out Assembly assembly)
        {
            CompilerOptions opt = new();
            CSharpCodeProvider provider = new(opt);


            string myTempFile = Path.Combine(Path.GetTempPath(), "re_script.cs");

            Misc.SendMessage("Compiling C# Script");
            CompilerParameters compileParameters = CompilerSettings(true); // When compiler is invoked from the editor it's always in debug mode
            CompilerResults results = provider.CompileAssemblyFromSource(compileParameters, source); // Compiling
            Misc.SendMessage("Compile Done");

            assembly = null;
            errorwarnings = new();
            bool has_error = ManageCompileResult(results, ref errorwarnings);
            if (has_error)
            {
                var error = results.Errors[0];
                var a = new SourceLocation(0, error.Line, error.Column);
                throw new SyntaxErrorException(error.ErrorText, results.PathToAssembly, error.ErrorNumber, "", new SourceSpan(a, a), 0, Severity.Error);
            }
            else
            {
                assembly = results.CompiledAssembly;
            }
            return has_error;
        }
        */

        // https://medium.com/swlh/replace-codedom-with-roslyn-but-bin-roslyn-csc-exe-not-found-6a5dd9290bf2
        // https://stackoverflow.com/questions/20018979/how-can-i-target-a-specific-language-version-using-codedom
        // https://docs.microsoft.com/it-it/dotnet/api/microsoft.csharp.csharpcodeprovider.-ctor?view=net-5.0
        // https://github.com/aspnet/RoslynCodeDomProvider/blob/main/src/Microsoft.CodeDom.Providers.DotNetCompilerPlatform/Util/IProviderOptions.cs
        // https://josephwoodward.co.uk/2016/12/in-memory-c-sharp-compilation-using-roslyn
        public bool CompileFromFile(string path, bool debug, out List<string> errorwarnings, out Assembly assembly)
        {
            errorwarnings = new();
            assembly = null;

            // If debug is true I check for the force release directive
            if (debug == true)
            {
                debug = (CheckForceReleaseDirective(path) != true); // If flag is true then debug is false
            }

            HashSet<string> filesList = new(StringComparer.InvariantCultureIgnoreCase) { }; // List of files.
            FindAllIncludedCSharpScript(path, filesList, errorwarnings);
            if (errorwarnings.Count > 0)
            {
                return true;
            }

            if (debug)
            {
                Misc.SendMessage("Compiling C# Script [DEBUG] " + Path.GetFileName(path));
            }
            else
            {
                Misc.SendMessage("Compiling C# Script [RELEASE] " + Path.GetFileName(path));
            }

            DateTime start = DateTime.Now;

            CompilerOptions m_opt = new();
            CSharpCodeProvider m_provider = new(m_opt);
            CompilerParameters m_compileParameters = CompilerSettings(true); 

            m_compileParameters.IncludeDebugInformation = debug;
            CompilerResults results = m_provider.CompileAssemblyFromFile(m_compileParameters, filesList.ToArray()); // Compiling

            DateTime stop = DateTime.Now;
            Misc.SendMessage("Script compiled in " + (stop - start).TotalMilliseconds.ToString("F0") + " ms");

            bool has_error = ManageCompileResult(results, errorwarnings);
            if (!has_error)
            {
                assembly = results.CompiledAssembly;
            }
            return has_error;
        }

        public void Execute(Assembly assembly)
        {
            // This is important for methods visibility. Check if all of these flags are really needed.
            BindingFlags bf = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.InvokeMethod;

            MethodInfo run = null;

            // Search trough all methods and finds Run then calls it
            int runMethodsFound = 0;
            foreach (Type mt in assembly.GetTypes())
            {
                if (mt != null)
                {
                    MethodInfo method = mt.GetMethod("Run", bf);
                    if (method != null) 
                    {
                        run = method;
                        runMethodsFound++;
                        if (runMethodsFound > 1)
                        {
                            string error = "Found more than one 'public void Run()' method in script.\nMust be only one Run method.";
                            Misc.SendMessage(error);
                            throw new Microsoft.Scripting.SyntaxErrorException(error, null, new SourceSpan(), 0, Severity.FatalError);
                        }
                    }
                }
            }

            // If Run method does not exists would be rised an exception later but better to throw a
            // SyntaxErrorException now and log it too
            if (run == null)
            {
                string error = "Required method 'public void Run()' missing from script.";
                Misc.SendMessage(error);
                throw new Microsoft.Scripting.SyntaxErrorException(error,null, new SourceSpan(), 0, Severity.FatalError);
            }

            // Creates an instance of the class runs the Run method
            object scriptInstance = Activator.CreateInstance(run.DeclaringType);

            run.Invoke(scriptInstance, null);
        }

    }
}
