using CommandLine;

namespace DTDL2OAS
{
    internal class Program
    {

        public class Options
        {
            [Option('s', "server", Default = "http://localhost:8080/", Required = false, HelpText = "The server URL (where presumably an API implementation is running).")]
            public string Server { get; set; }
            [Option('i', "inputPath", Required = true, HelpText = "The path to the on-disk root ontology directory or file to translate.")]
            public string InputPath { get; set; }
            [Option('m', "mappingsPath", Required = true, HelpText = "Path to mappings CSV file matching endpoint names to DTDL Interfaces.")]
            public string MappingsPath { get; set; }
            [Option('o', "outputPath", Required = true, HelpText = "The path at which to put the generated OAS file.")]
            public string OutputPath { get; set; }
        }

        // Configuration fields
        private static string _server;
        private static string _inputPath;
        private static string _mappingsPath;
        private static string _outputPath;

        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                   .WithParsed(o =>
                   {
                       _server = o.Server;
                       _inputPath = o.InputPath;
                       _mappingsPath = o.MappingsPath;
                       _outputPath = o.OutputPath;
                   })
                   .WithNotParsed((errs) =>
                   {
                       Environment.Exit(1);
                   });

            Console.WriteLine("Hello, World!");
        }
    }
}