using CommandLine;
using Microsoft.Azure.DigitalTwins.Parser;
using Microsoft.Azure.DigitalTwins.Parser.Models;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DTDL2OAS
{
    internal class Program
    {

        public class Options
        {
            [Option('s', "server", Default = "http://localhost:8080/", Required = false, HelpText = "The server URL (where presumably an API implementation is running).")]
            public string Server { get; set; }
            [Option('i', "inputPath", Required = true, HelpText = "The path to the ontology root directory or file to translate.")]
            public string InputPath { get; set; }
            [Option('a', "annotationsPath", Required = true, HelpText = "Path to ontology annotations file detaililing, e.g., version, license, etc.")]
            public string AnnotationsPath { get; set; }
            [Option('m', "mappingsPath", Required = true, HelpText = "Path to mappings CSV file matching endpoint names to DTDL Interfaces.")]
            public string MappingsPath { get; set; }
            [Option('n', "namespaceAbbreviationsPath", Required = false, HelpText = "Path to file mapping ontology namespace prefixes to abbreviations.")]
            public string NamespaceAbbreviationsPath { get; set; }
            [Option('o', "outputPath", Required = true, HelpText = "The path at which to put the generated OAS file.")]
            public string OutputPath { get; set; }
        }

        // Configuration fields
        private static string _server;
        private static string _inputPath;
        private static string _annotationsPath;
        private static string _mappingsPath;
        private static string _namespaceAbbreviationsPath;
        private static string _outputPath;

        // Data fields
        private static IReadOnlyDictionary<Dtmi, DTEntityInfo> DTEntities;
        private static Dictionary<string, string> OntologyAnnotations = new();
        private static Dictionary<string, string> NamespaceAbbreviations = new();
        private static Dictionary<string, Dtmi> EndpointMappings = new();
        private static OASDocument OutputDocument;

        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                   .WithParsed(o =>
                   {
                       _server = o.Server;
                       _inputPath = o.InputPath;
                       _annotationsPath = o.AnnotationsPath;
                       _mappingsPath = o.MappingsPath;
                       _namespaceAbbreviationsPath = o.NamespaceAbbreviationsPath;
                       _outputPath = o.OutputPath;
                   })
                   .WithNotParsed((errs) =>
                   {
                       Environment.Exit(1);
                   });

            LoadInput();
            LoadAnnotations();
            LoadMappings();
            if (_namespaceAbbreviationsPath != null)
                LoadNamespaceAbbreviations();

            // Create OAS object, create OAS info header, server block, (empty) components/schemas structure, and LoadedOntologies endpoint
            OutputDocument = new OASDocument
            {
                info = GenerateDocumentInfo(),
                servers = new List<Dictionary<string, string>> { new Dictionary<string, string> { { "url", _server } } },
                components = new OASDocument.Components(),
                paths = new Dictionary<string, OASDocument.Path>()
            };

            GenerateSchemas();
            GeneratePaths();

            // Dump output as YAML
            var serializer = new SerializerBuilder()
                .DisableAliases()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();
            var yaml = serializer.Serialize(OutputDocument);
            File.WriteAllText(_outputPath, yaml);
        }

        // Load a file or a directory of files from disk
        private static void LoadInput()
        {
            // Get selected file or, if directory selected, all JSON files in selected dir
            IEnumerable<FileInfo> sourceFiles;
            if (File.GetAttributes(_inputPath) == FileAttributes.Directory)
            {
                DirectoryInfo directoryInfo = new DirectoryInfo(_inputPath);
                sourceFiles = directoryInfo.EnumerateFiles("*.json", SearchOption.AllDirectories);
            }
            else
            {
                FileInfo singleSourceFile = new FileInfo(_inputPath);
                sourceFiles = new[] { singleSourceFile };
            }


            List<string> modelJson = new List<string>();
            foreach (FileInfo file in sourceFiles)
            {
                using StreamReader modelReader = new StreamReader(file.FullName);
                modelJson.Add(modelReader.ReadToEnd());
            }
            ModelParser modelParser = new ModelParser(0);

            try
            {
                DTEntities = modelParser.Parse(modelJson);
            }
            catch (ParsingException parserEx)
            {
                Console.Error.WriteLine(parserEx.Message);
                Console.Error.WriteLine(string.Join("\n\n", parserEx.Errors.Select(error => error.Message)));
                Environment.Exit(1);
            }
        }

        private static void LoadAnnotations()
        {
            using (var reader = new StreamReader(_annotationsPath))
            {
                string[] annotations = reader.ReadToEnd().Split(Environment.NewLine);
                foreach (string annotation in annotations)
                {
                    // Last line of file might be empty
                    if (annotation.Length == 0) continue;
                    string annotationKey = annotation.Split('=').First();
                    string annotationValue = annotation.Substring(annotationKey.Length + 1);
                    OntologyAnnotations.Add(annotationKey, annotationValue);
                }
            }

            string[] mandatoryAnnotations = new string[]
            {
                "title",
                "version",
                "licenseName"
            };

            foreach (string mandatoryAnnotation in mandatoryAnnotations)
            {
                if (!OntologyAnnotations.ContainsKey(mandatoryAnnotation))
                {
                    Console.Error.WriteLine($"Mandatory ontology annotation '{mandatoryAnnotation}' is undefined.");
                    Environment.Exit(1);
                }
            }
        }

        private static void LoadMappings()
        {
            using (var reader = new StreamReader(_mappingsPath))
            {
                string mappingsCsv = reader.ReadToEnd();
                string[] mappings = mappingsCsv.Split(Environment.NewLine);
                // Skipping first row b/c headers
                for (int i = 1; i < mappings.Length; i++)
                {
                    string mapping = mappings[i];
                    // Last line of file might be empty
                    if (mapping.Length == 0) continue;
                    string mappedEndpoint = mapping.Split(';').First();
                    string mappedDtmiString = mapping.Substring(mappedEndpoint.Length + 1).Trim('\"');
                    Dtmi mappedDtmi = new Dtmi(mappedDtmiString);
                    if (DTEntities.ContainsKey(mappedDtmi))
                    {
                        EndpointMappings.Add(mappedEndpoint, mappedDtmi);
                    }
                }
            }
        }

        private static void LoadNamespaceAbbreviations()
        {
            using (var reader = new StreamReader(_namespaceAbbreviationsPath))
            {
                string[] abbreviationMappings = reader.ReadToEnd().Split(Environment.NewLine);
                foreach (string abbreviationMapping in abbreviationMappings)
                {
                    // Last line of file might be empty
                    if (abbreviationMapping.Length == 0) continue;
                    string namespacePrefix = abbreviationMapping.Split('=').First();
                    string namespaceAbbreviation = abbreviationMapping.Substring(namespacePrefix.Length + 1);
                    NamespaceAbbreviations.Add(namespacePrefix, namespaceAbbreviation);
                }
            }
        }

        private static OASDocument.Info GenerateDocumentInfo()
        {
            OASDocument.Info docInfo = new OASDocument.Info();

            // Mandatory parts
            docInfo.title = OntologyAnnotations["title"];
            docInfo.version = OntologyAnnotations["version"];

            // Licensing is non-mandatory; but if license element exists, name is mandatory
            if (OntologyAnnotations.ContainsKey("licenseName"))
            {
                docInfo.license = new OASDocument.License();
                docInfo.license.name = OntologyAnnotations["licenseName"];
                if (OntologyAnnotations.ContainsKey("licenseUrl"))
                {
                    docInfo.license.url = OntologyAnnotations["licenseUrl"];
                }
            }

            // Contact information; non-mandatory
            if (OntologyAnnotations.ContainsKey("contactName") || OntologyAnnotations.ContainsKey("contactUrl") || OntologyAnnotations.ContainsKey("contactEmail"))
            {
                docInfo.contact = new OASDocument.Contact();
                if (OntologyAnnotations.ContainsKey("contactName"))
                {
                    docInfo.contact.name = OntologyAnnotations["contactName"];
                }
                if (OntologyAnnotations.ContainsKey("contactUrl"))
                {
                    docInfo.contact.url = OntologyAnnotations["contactUrl"];
                }
                if (OntologyAnnotations.ContainsKey("contactEmail"))
                {
                    docInfo.contact.email = OntologyAnnotations["contactEmail"];
                }
            }

            // Description; non-mandatory
            if (OntologyAnnotations.ContainsKey("description"))
            {
                docInfo.description = OntologyAnnotations["description"];
            }

            return docInfo;
        }

        private static void GenerateSchemas()
        {
            foreach (Dtmi dtmi in EndpointMappings.Values)
            {
                DTInterfaceInfo dtInterface = (DTInterfaceInfo)DTEntities[dtmi];
                
                // Get schema name
                string schemaName = GetApiName(dtInterface).Replace(":", "_", StringComparison.Ordinal);

                // Create schema for class and corresponding properties dict
                OASDocument.ComplexSchema schema = new OASDocument.ComplexSchema();
                schema.properties = new Dictionary<string, OASDocument.Schema>();

                // Add @id for all entries
                OASDocument.PrimitiveSchema idSchema = new OASDocument.PrimitiveSchema();
                idSchema.type = "string";
                schema.properties.Add("@id", idSchema);

                // Add @type for all entries
                OASDocument.PrimitiveSchema typeSchema = new OASDocument.PrimitiveSchema
                {
                    type = "string",
                    DefaultValue = dtInterface.Id.AbsoluteUri
                };
                schema.properties.Add("@type", typeSchema);

                // TODO: Create schema contents

                // Append schema to output document
                OutputDocument.components.schemas.Add(schemaName, schema);
            }
        }

        private static void GeneratePaths()
        {
            // Iterate over all classes
            foreach (KeyValuePair<string,Dtmi> endpointMapping in EndpointMappings)
            {
                // Get key name for API
                string endpointName = endpointMapping.Key;
                DTInterfaceInfo dtInterface = (DTInterfaceInfo)DTEntities[endpointMapping.Value];
                string interfaceLabel = GetDocumentationName(dtInterface);
                string interfaceSchemaName = GetApiName(dtInterface);

                // Create paths and corresponding operations for class
                OutputDocument.paths.Add($"/{endpointName}", new OASDocument.Path
                {
                    get = OperationGenerators.GenerateGetEntitiesOperation(endpointName, interfaceLabel/*, oClass*/),
                    post = OperationGenerators.GeneratePostEntityOperation(endpointName, interfaceSchemaName, interfaceLabel)
                });
                OutputDocument.paths.Add($"/{endpointName}/{{id}}", new OASDocument.Path
                {
                    get = OperationGenerators.GenerateGetEntityByIdOperation(endpointName, interfaceSchemaName, interfaceLabel),
                    patch = OperationGenerators.GeneratePatchToIdOperation(endpointName, interfaceLabel),
                    put = OperationGenerators.GeneratePutToIdOperation(endpointName, interfaceLabel),
                    delete = OperationGenerators.GenerateDeleteByIdOperation(endpointName, interfaceLabel)
                });
            }
        }

        private static string GetDocumentationName(DTInterfaceInfo interfaceInfo)
        {
            IReadOnlyDictionary<string, string> displayNames = interfaceInfo.DisplayName;
            if (displayNames.ContainsKey(""))
                return displayNames[""];
            if (displayNames.ContainsKey("en"))
                return displayNames["en"];
            if (displayNames.Count > 0)
                return displayNames.First().Value;
            return interfaceInfo.Id.Versionless;
        }

        private static string GetApiName(DTEntityInfo entityInfo)
        {
            if (entityInfo is DTNamedEntityInfo)
                return (entityInfo as DTNamedEntityInfo).Name;

            string versionLessDtmi = entityInfo.Id.Versionless;
            string entityNamespace = versionLessDtmi.Substring(0, versionLessDtmi.LastIndexOf(':'));
            string entityId = versionLessDtmi.Substring(versionLessDtmi.LastIndexOf(':') + 1);
            if (NamespaceAbbreviations.ContainsKey(entityNamespace))
                return NamespaceAbbreviations[entityNamespace] + ":" + entityId;

            return versionLessDtmi.Split(':').Last();
        }
    }
}