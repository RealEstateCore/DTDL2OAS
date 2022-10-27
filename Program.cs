using CommandLine;
using Microsoft.Azure.DigitalTwins.Parser;
using Microsoft.Azure.DigitalTwins.Parser.Models;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DTDL2OAS
{
    internal class Program
    {

        public class Options
        {
            [Option('s', "server", Default = "http://localhost:8080/", Required = false, HelpText = "The server URL (where presumably an API implementation is running).")]
            public string? Server { get; set; }
            [Option('i', "inputPath", Required = true, HelpText = "The path to the ontology root directory or file to translate.")]
            public string? InputPath { get; set; }
            [Option('n', "nuspecPath", Required = true, HelpText = "Path to .nuspec file holding package annotations detailing, e.g., version, license, etc.")]
            public string? NuspecPath { get; set; }
            /*[Option('a', "annotationsPath", Required = true, HelpText = "Path to ontology annotations file detaililing, e.g., version, license, etc.")]
            public string AnnotationsPath { get; set; }*/
            [Option('e', "endpointsPath", Required = true, HelpText = "Path to mappings CSV file matching REST endpoint names to DTDL Interfaces.")]
            public string EndpointsPath { get; set; }
            /*[Option('n', "namespaceAbbreviationsPath", Required = false, HelpText = "Path to file mapping ontology namespace prefixes to abbreviations.")]
            public string NamespaceAbbreviationsPath { get; set; }*/
            [Option('o', "outputPath", Required = true, HelpText = "The path at which to put the generated OAS and context files.")]
            public string? OutputPath { get; set; }
        }

        // Configuration fields
        private static string _server = string.Empty;
        private static string _inputPath = string.Empty;
        //private static string _annotationsPath;
        private static string _endpointsPath = String.Empty;
        //private static string _namespaceAbbreviationsPath;
        private static string _nuspecPath = string.Empty;
        private static string _outputPath = string.Empty;

        // Data fields
        private static IReadOnlyDictionary<Dtmi, DTEntityInfo> DTEntities;
        private static Dictionary<string, string> OntologyAnnotations = new();
        //private static Dictionary<string, string> NamespaceAbbreviations = new();
        private static Dictionary<string, Dtmi> EndpointMappings = new();
        private static OASDocument OutputDocument;

        /// <summary>
        /// Dictionary mapping DTDL schema types to corresponding OSA data types and formats, see
        /// https://github.com/OAI/OpenAPI-Specification/blob/master/versions/3.0.0.md#dataTypeFormat
        /// </summary>
        public static readonly Dictionary<Type, (string, string)> dtdlOsaMappings = new Dictionary<Type, (string, string)>
        {
            {typeof(DTBooleanInfo) ,("boolean","") },
            {typeof(DTDateInfo),("string","date") },
            {typeof(DTDateTimeInfo),("string","date-time") },
            {typeof(DTDoubleInfo),("number","double") },
            {typeof(DTFloatInfo),("number","float") },
            {typeof(DTIntegerInfo),("integer","int32") },
            {typeof(DTLongInfo),("integer","int64") },
            {typeof(DTStringInfo),("string","") }
        };

        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                   .WithParsed(o =>
                   {
                       _server = o.Server;
                       _inputPath = o.InputPath;
                       /*_annotationsPath = o.AnnotationsPath;
                       _mappingsPath = o.MappingsPath;
                       _namespaceAbbreviationsPath = o.NamespaceAbbreviationsPath;*/
                       _endpointsPath = o.EndpointsPath;
                       _nuspecPath = o.NuspecPath;
                       _outputPath = o.OutputPath;
                   })
                   .WithNotParsed((errs) =>
                   {
                       Environment.Exit(1);
                   });

            LoadInput();
            LoadNuspec();
            LoadEndpointMappings();

            /*
            if (_namespaceAbbreviationsPath != null)
            {
                LoadNamespaceAbbreviations();
            }*/

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

            // Generate and add context schema
            OutputDocument.components.schemas.Add("Context", GenerateContextSchema());

            // Dump output as YAML
            var serializer = new SerializerBuilder()
                .DisableAliases()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
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

        private static void LoadNuspec()
        {
            using var reader = new StreamReader(_nuspecPath);
            using var xmlReader = XmlReader.Create(reader);

            XmlDocument doc = new XmlDocument();
            doc.Load(xmlReader);
            XmlNamespaceManager namespaces = new XmlNamespaceManager(doc.NameTable);
            namespaces.AddNamespace("nuspec", "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd");
            XmlElement docRoot = doc.DocumentElement;
            // TODO: Sort out all the nullable warnings here

            OntologyAnnotations.Add("id", docRoot.SelectSingleNode("/nuspec:package/nuspec:metadata/nuspec:id/text()", namespaces).Value);
            OntologyAnnotations.Add("version", docRoot.SelectSingleNode("/nuspec:package/nuspec:metadata/nuspec:version/text()", namespaces).Value);
            OntologyAnnotations.Add("description", docRoot.SelectSingleNode("/nuspec:package/nuspec:metadata/nuspec:description/text()", namespaces).Value);
            OntologyAnnotations.Add("authors", docRoot.SelectSingleNode("/nuspec:package/nuspec:metadata/nuspec:authors/text()", namespaces).Value);

            // TODO: Generically parse non-mandatory fields
        }

        private static void LoadEndpointMappings()
        {
            using (var reader = new StreamReader(_endpointsPath))
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

        private static OASDocument.Info GenerateDocumentInfo()
        {
            OASDocument.Info docInfo = new OASDocument.Info();

            // Mandatory parts
            docInfo.title = OntologyAnnotations.ContainsKey("title") ? OntologyAnnotations["title"] : OntologyAnnotations["id"];
            docInfo.version = OntologyAnnotations["version"];
            docInfo.description = OntologyAnnotations["description"];
            docInfo.contact = new OASDocument.Contact() { name = OntologyAnnotations["authors"] };

            // TODO: Set non-mandatory info

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

                // Create relationship-based schema properties
                foreach (DTRelationshipInfo relationship in dtInterface.AllRelationships())
                {
                    string relationshipName = GetApiName(relationship);

                    // Set the type of the property; locally defined named classes can be either URI or full schema representation
                    OASDocument.ComplexSchema relationshipSchema = new OASDocument.ComplexSchema
                    {
                        properties = new Dictionary<string, OASDocument.Schema> {
                                        { "@id", new OASDocument.PrimitiveSchema { type = "string" } },
                                        { "@type", new OASDocument.PrimitiveSchema { type = "string" } }
                                    },
                        required = new List<string> { "@id" }
                    };
                    if (relationship.Target != null)
                    {
                        ((OASDocument.PrimitiveSchema)relationshipSchema.properties["@type"]).DefaultValue = relationship.Target.ToString();
                    }

                    if (relationship.MaxMultiplicity == 1)
                    {
                        schema.properties[relationshipName] = relationshipSchema;
                    }
                    else
                    {
                        OASDocument.ArraySchema propertyArraySchema = new OASDocument.ArraySchema();
                        propertyArraySchema.items = relationshipSchema;
                        if (relationship.MinMultiplicity.HasValue)
                        {
                            propertyArraySchema.minItems = relationship.MinMultiplicity.Value;
                        }
                        if (relationship.MaxMultiplicity.HasValue)
                        {
                            propertyArraySchema.maxItems = relationship.MaxMultiplicity.Value;
                        }
                        schema.properties[relationshipName] = propertyArraySchema;

                    }
                }
                foreach (DTPropertyInfo property in dtInterface.AllProperties())
                {
                    string propertyName = GetApiName(property);

                    // Fall back to string representation for complex schemas
                    OASDocument.PrimitiveSchema outputPropertySchema = new OASDocument.PrimitiveSchema()
                    {
                        type = "string"
                    };

                    // Check the schema type against predefined mapping
                    if (property.Schema is DTPrimitiveSchemaInfo)
                    {
                        Type schemaType = property.Schema.GetType();
                        if (Program.dtdlOsaMappings.ContainsKey(schemaType))
                        {
                            outputPropertySchema.type = Program.dtdlOsaMappings[schemaType].Item1;
                            string format = Program.dtdlOsaMappings[schemaType].Item2;
                            if (format.Length > 0)
                            {
                                outputPropertySchema.format = format;
                            }
                        }
                    }

                    schema.properties[propertyName] = outputPropertySchema;
                }
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
                    get = OperationGenerators.GenerateGetEntitiesOperation(endpointName, dtInterface, interfaceSchemaName, interfaceLabel),
                    post = OperationGenerators.GeneratePostEntityOperation(endpointName, interfaceSchemaName, interfaceLabel)
                });
                OutputDocument.paths.Add($"/{endpointName}/{{id}}", new OASDocument.Path
                {
                    get = OperationGenerators.GenerateGetEntityByIdOperation(endpointName, interfaceSchemaName, interfaceLabel),
                    patch = OperationGenerators.GeneratePatchToIdOperation(endpointName, interfaceSchemaName, interfaceLabel),
                    put = OperationGenerators.GeneratePutToIdOperation(endpointName, interfaceSchemaName, interfaceLabel),
                    delete = OperationGenerators.GenerateDeleteByIdOperation(endpointName, interfaceLabel)
                });
            }
        }

        private static OASDocument.Schema GenerateContextSchema()
        {
            // Set @context/@base (default data namespace)
            OASDocument.PrimitiveSchema baseNamespaceSchema = new OASDocument.PrimitiveSchema
            {
                type = "string",
                format = "uri"
            };
            // Hardcoded Hydra
            OASDocument.PrimitiveSchema hydraSchema = new OASDocument.PrimitiveSchema
            {
                type = "string",
                format = "uri",
                DefaultValue = "http://www.w3.org/ns/hydra/core#"
            };
            // Mash it all together into a @context block
            OASDocument.ComplexSchema contextSchema = new OASDocument.ComplexSchema
            {
                required = new List<string> { "@vocab", "@base", "hydra" },
                properties = new Dictionary<string, OASDocument.Schema> {
                    { "@base", baseNamespaceSchema },
                    { "hydra", hydraSchema },
                }
            };
            return contextSchema;
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

        public static string GetApiName(DTEntityInfo entityInfo)
        {
            if (entityInfo is DTNamedEntityInfo)
                return (entityInfo as DTNamedEntityInfo).Name;

            string versionLessDtmi = entityInfo.Id.Versionless;
            string entityNamespace = versionLessDtmi.Substring(0, versionLessDtmi.LastIndexOf(':'));
            string entityId = versionLessDtmi.Substring(versionLessDtmi.LastIndexOf(':') + 1);

            return versionLessDtmi.Split(':').Last();
        }
    }
}