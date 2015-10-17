﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.JsonParser.Sources;
using Microsoft.Extensions.ProjectModel.Graph;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Microsoft.Extensions.ProjectModel
{
    public class ProjectReader
    {
        public static Project GetProject(string projectFile)
        {
            return GetProject(projectFile, new List<DiagnosticMessage>());
        }

        public static Project GetProject(string projectFile, ICollection<DiagnosticMessage> diagnostics)
        {
            var name = Path.GetFileName(Path.GetDirectoryName(projectFile));
            using (var stream = new FileStream(projectFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return new ProjectReader().ReadProject(stream, name, projectFile, diagnostics);
            }
        }

        public Project ReadProject(Stream stream, string projectName, string projectPath, ICollection<DiagnosticMessage> diagnostics)
        {
            var project = new Project();

            var reader = new StreamReader(stream);
            var rawProject = JsonDeserializer.Deserialize(reader) as JsonObject;
            if (rawProject == null)
            {
                throw FileFormatException.Create(
                    "The JSON file can't be deserialized to a JSON object.",
                    projectPath);
            }

            // Meta-data properties
            project.Name = projectName;
            project.ProjectFilePath = Path.GetFullPath(projectPath);

            var version = rawProject.Value("version") as JsonString;
            if (version == null)
            {
                project.Version = new NuGetVersion("1.0.0");
            }
            else
            {
                try
                {
                    var buildVersion = Environment.GetEnvironmentVariable("DOETNET_BUILD_VERSION");
                    project.Version = SpecifySnapshot(version, buildVersion);
                }
                catch (Exception ex)
                {
                    throw FileFormatException.Create(ex, version, project.ProjectFilePath);
                }
            }

            var fileVersion = Environment.GetEnvironmentVariable("DOTNET_ASSEMBLY_FILE_VERSION");
            if (string.IsNullOrWhiteSpace(fileVersion))
            {
                project.AssemblyFileVersion = project.Version.Version;
            }
            else
            {
                try
                {
                    var simpleVersion = project.Version.Version;
                    project.AssemblyFileVersion = new Version(simpleVersion.Major,
                        simpleVersion.Minor,
                        simpleVersion.Build,
                        int.Parse(fileVersion));
                }
                catch (FormatException ex)
                {
                    throw new FormatException("The assembly file version is invalid: " + fileVersion, ex);
                }
            }

            project.Description = rawProject.ValueAsString("description");
            project.Summary = rawProject.ValueAsString("summary");
            project.Copyright = rawProject.ValueAsString("copyright");
            project.Title = rawProject.ValueAsString("title");
            project.WebRoot = rawProject.ValueAsString("webroot");
            project.EntryPoint = rawProject.ValueAsString("entryPoint");
            project.ProjectUrl = rawProject.ValueAsString("projectUrl");
            project.LicenseUrl = rawProject.ValueAsString("licenseUrl");
            project.IconUrl = rawProject.ValueAsString("iconUrl");

            project.Authors = rawProject.ValueAsStringArray("authors") ?? Array.Empty<string>();
            project.Owners = rawProject.ValueAsStringArray("owners") ?? Array.Empty<string>();
            project.Tags = rawProject.ValueAsStringArray("tags") ?? Array.Empty<string>();

            project.Language = rawProject.ValueAsString("language");
            project.ReleaseNotes = rawProject.ValueAsString("releaseNotes");

            project.RequireLicenseAcceptance = rawProject.ValueAsBoolean("requireLicenseAcceptance", defaultValue: false);
            project.IsLoadable = rawProject.ValueAsBoolean("loadable", defaultValue: true);
            // TODO: Move this to the dependencies node
            project.EmbedInteropTypes = rawProject.ValueAsBoolean("embedInteropTypes", defaultValue: false);

            project.Dependencies = new List<LibraryRange>();

            // Project files
            project.Files = new ProjectFilesCollection(rawProject, project.ProjectDirectory, project.ProjectFilePath);

            var commands = rawProject.Value("commands") as JsonObject;
            if (commands != null)
            {
                foreach (var key in commands.Keys)
                {
                    var value = commands.ValueAsString(key);
                    if (value != null)
                    {
                        project.Commands[key] = value;
                    }
                }
            }

            var scripts = rawProject.Value("scripts") as JsonObject;
            if (scripts != null)
            {
                foreach (var key in scripts.Keys)
                {
                    var stringValue = scripts.ValueAsString(key);
                    if (stringValue != null)
                    {
                        project.Scripts[key] = new string[] { stringValue };
                        continue;
                    }

                    var arrayValue = scripts.ValueAsStringArray(key);
                    if (arrayValue != null)
                    {
                        project.Scripts[key] = arrayValue;
                        continue;
                    }

                    throw FileFormatException.Create(
                        string.Format("The value of a script in {0} can only be a string or an array of strings", Project.FileName),
                        scripts.Value(key),
                        project.ProjectFilePath);
                }
            }

            BuildTargetFrameworksAndConfigurations(project, rawProject, diagnostics);

            PopulateDependencies(
                project.ProjectFilePath,
                project.Dependencies,
                rawProject,
                "dependencies",
                isGacOrFrameworkReference: false);

            return project;
        }

        private static NuGetVersion SpecifySnapshot(string version, string snapshotValue)
        {
            if (version.EndsWith("-*"))
            {
                if (string.IsNullOrEmpty(snapshotValue))
                {
                    version = version.Substring(0, version.Length - 2);
                }
                else
                {
                    version = version.Substring(0, version.Length - 1) + snapshotValue;
                }
            }

            return new NuGetVersion(version);
        }

        private static void PopulateDependencies(
            string projectPath,
            IList<LibraryRange> results,
            JsonObject settings,
            string propertyName,
            bool isGacOrFrameworkReference)
        {
            var dependencies = settings.ValueAsJsonObject(propertyName);
            if (dependencies != null)
            {
                foreach (var dependencyKey in dependencies.Keys)
                {
                    if (string.IsNullOrEmpty(dependencyKey))
                    {
                        throw FileFormatException.Create(
                            "Unable to resolve dependency ''.",
                            dependencies.Value(dependencyKey),
                            projectPath);
                    }

                    var dependencyValue = dependencies.Value(dependencyKey);
                    var dependencyTypeValue = LibraryDependencyType.Default;
                    JsonString dependencyVersionAsString = null;
                    LibraryType target = isGacOrFrameworkReference ? LibraryType.ReferenceAssembly : LibraryType.Unspecified;

                    if (dependencyValue is JsonObject)
                    {
                        // "dependencies" : { "Name" : { "version": "1.0", "type": "build", "target": "project" } }
                        var dependencyValueAsObject = (JsonObject)dependencyValue;
                        dependencyVersionAsString = dependencyValueAsObject.ValueAsString("version");

                        var type = dependencyValueAsObject.ValueAsString("type");
                        if (type != null)
                        {
                            dependencyTypeValue = LibraryDependencyType.Parse(type.Value);
                        }

                        // Read the target if specified
                        if (!isGacOrFrameworkReference)
                        {
                            LibraryType parsedTarget;
                            var targetStr = dependencyValueAsObject.ValueAsString("target");
                            if (!string.IsNullOrEmpty(targetStr) && LibraryType.TryParse(targetStr, out parsedTarget))
                            {
                                target = parsedTarget;
                            }
                        }
                    }
                    else if (dependencyValue is JsonString)
                    {
                        // "dependencies" : { "Name" : "1.0" }
                        dependencyVersionAsString = (JsonString)dependencyValue;
                    }
                    else
                    {
                        throw FileFormatException.Create(
                            string.Format("Invalid dependency version: {0}. The format is not recognizable.", dependencyKey),
                            dependencyValue,
                            projectPath);
                    }

                    VersionRange dependencyVersionRange = null;
                    if (!string.IsNullOrEmpty(dependencyVersionAsString?.Value))
                    {
                        try
                        {
                            dependencyVersionRange = VersionRange.Parse(dependencyVersionAsString.Value);
                        }
                        catch (Exception ex)
                        {
                            throw FileFormatException.Create(
                                ex,
                                dependencyValue,
                                projectPath);
                        }
                    }

                    results.Add(new LibraryRange(
                        dependencyKey,
                        dependencyVersionRange,
                        target,
                        dependencyTypeValue,
                        projectPath,
                        dependencies.Value(dependencyKey).Line,
                        dependencies.Value(dependencyKey).Column));
                }
            }
        }

        private static bool TryGetStringEnumerable(JsonObject parent, string property, out IEnumerable<string> result)
        {
            var collection = new List<string>();
            var valueInString = parent.ValueAsString(property);
            if (valueInString != null)
            {
                collection.Add(valueInString);
            }
            else
            {
                var valueInArray = parent.ValueAsStringArray(property);
                if (valueInArray != null)
                {
                    collection.AddRange(valueInArray);
                }
                else
                {
                    result = null;
                    return false;
                }
            }

            result = collection.SelectMany(value => value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries));
            return true;
        }

        private void BuildTargetFrameworksAndConfigurations(Project project, JsonObject projectJsonObject, ICollection<DiagnosticMessage> diagnostics)
        {
            // Get the shared compilationOptions
            project._defaultCompilerOptions = GetCompilationOptions(projectJsonObject) ?? new CompilerOptions();

            project._defaultTargetFrameworkConfiguration = new TargetFrameworkInformation
            {
                Dependencies = new List<LibraryRange>()
            };

            // Add default configurations
            project._compilerOptionsByConfiguration["Debug"] = new CompilerOptions
            {
                Defines = new[] { "DEBUG", "TRACE" },
                Optimize = false
            };

            project._compilerOptionsByConfiguration["Release"] = new CompilerOptions
            {
                Defines = new[] { "RELEASE", "TRACE" },
                Optimize = true
            };

            // The configuration node has things like debug/release compiler settings
            /*
                {
                    "configurations": {
                        "Debug": {
                        },
                        "Release": {
                        }
                    }
                }
            */

            var configurationsSection = projectJsonObject.ValueAsJsonObject("configurations");
            if (configurationsSection != null)
            {
                foreach (var configKey in configurationsSection.Keys)
                {
                    var compilerOptions = GetCompilationOptions(configurationsSection.ValueAsJsonObject(configKey));

                    // Only use this as a configuration if it's not a target framework
                    project._compilerOptionsByConfiguration[configKey] = compilerOptions;
                }
            }

            // The frameworks node is where target frameworks go
            /*
                {
                    "frameworks": {
                        "net45": {
                        },
                        "dnxcore50": {
                        }
                    }
                }
            */

            var frameworks = projectJsonObject.ValueAsJsonObject("frameworks");
            if (frameworks != null)
            {
                foreach (var frameworkKey in frameworks.Keys)
                {
                    try
                    {
                        var frameworkToken = frameworks.ValueAsJsonObject(frameworkKey);
                        var success = BuildTargetFrameworkNode(project, frameworkKey, frameworkToken);
                        if (!success)
                        {
                            diagnostics?.Add(
                                new DiagnosticMessage(
                                    ErrorCodes.NU1008,
                                    $"\"{frameworkKey}\" is an unsupported framework.",
                                    project.ProjectFilePath,
                                    DiagnosticMessageSeverity.Error,
                                    frameworkToken.Line,
                                    frameworkToken.Column));
                        }
                    }
                    catch (Exception ex)
                    {
                        throw FileFormatException.Create(ex, frameworks.Value(frameworkKey), project.ProjectFilePath);
                    }
                }
            }
        }

        /// <summary>
        /// Parse a Json object which represents project configuration for a specified framework
        /// </summary>
        /// <param name="frameworkKey">The name of the framework</param>
        /// <param name="frameworkValue">The Json object represent the settings</param>
        /// <returns>Returns true if it successes.</returns>
        private bool BuildTargetFrameworkNode(Project project, string frameworkKey, JsonObject frameworkValue)
        {
            // If no compilation options are provided then figure them out from the node
            var compilerOptions = GetCompilationOptions(frameworkValue) ??
                                  new CompilerOptions();

            var frameworkName = NuGetFramework.Parse(frameworkKey);

            // If it's not unsupported then keep it
            if (frameworkName.IsUnsupported)
            {
                // REVIEW: Should we skip unsupported target frameworks
                return false;
            }

            // Add the target framework specific define
            var defines = new HashSet<string>(compilerOptions.Defines ?? Enumerable.Empty<string>());
            var frameworkDefine = MakeDefaultTargetFrameworkDefine(frameworkName);

            if (!string.IsNullOrEmpty(frameworkDefine))
            {
                defines.Add(frameworkDefine);
            }

            compilerOptions.Defines = defines;

            var targetFrameworkInformation = new TargetFrameworkInformation
            {
                FrameworkName = frameworkName,
                Dependencies = new List<LibraryRange>()
            };

            var frameworkDependencies = new List<LibraryRange>();

            PopulateDependencies(
                project.ProjectFilePath,
                frameworkDependencies,
                frameworkValue,
                "dependencies",
                isGacOrFrameworkReference: false);

            var frameworkAssemblies = new List<LibraryRange>();
            PopulateDependencies(
                project.ProjectFilePath,
                frameworkAssemblies,
                frameworkValue,
                "frameworkAssemblies",
                isGacOrFrameworkReference: true);

            frameworkDependencies.AddRange(frameworkAssemblies);
            targetFrameworkInformation.Dependencies = frameworkDependencies;

            targetFrameworkInformation.WrappedProject = frameworkValue.ValueAsString("wrappedProject");

            var binNode = frameworkValue.ValueAsJsonObject("bin");
            if (binNode != null)
            {
                targetFrameworkInformation.AssemblyPath = binNode.ValueAsString("assembly");
                targetFrameworkInformation.PdbPath = binNode.ValueAsString("pdb");
            }

            project._compilerOptionsByFramework[frameworkName] = compilerOptions;
            project._targetFrameworks[frameworkName] = targetFrameworkInformation;

            return true;
        }

        private static CompilerOptions GetCompilationOptions(JsonObject rawObject)
        {
            var rawOptions = rawObject.ValueAsJsonObject("compilationOptions");
            if (rawOptions == null)
            {
                return null;
            }

            return new CompilerOptions
            {
                Defines = rawOptions.ValueAsStringArray("define"),
                LanguageVersion = rawOptions.ValueAsString("languageVersion"),
                AllowUnsafe = rawOptions.ValueAsNullableBoolean("allowUnsafe"),
                Platform = rawOptions.ValueAsString("platform"),
                WarningsAsErrors = rawOptions.ValueAsNullableBoolean("warningsAsErrors"),
                Optimize = rawOptions.ValueAsNullableBoolean("optimize"),
                KeyFile = rawOptions.ValueAsString("keyFile"),
                DelaySign = rawOptions.ValueAsNullableBoolean("delaySign"),
                StrongName = rawOptions.ValueAsNullableBoolean("strongName"),
                EmitEntryPoint = rawOptions.ValueAsNullableBoolean("emitEntryPoint")
            };
        }

        private static string MakeDefaultTargetFrameworkDefine(NuGetFramework targetFramework)
        {
            var shortName = targetFramework.GetTwoDigitShortFolderName();

            if (targetFramework.IsPCL)
            {
                return null;
            }

            var candidateName = shortName.ToUpperInvariant();

            // Replace '-', '.', and '+' in the candidate name with '_' because TFMs with profiles use those (like "net40-client")
            // and we want them representable as defines (i.e. "NET40_CLIENT")
            candidateName = candidateName.Replace('-', '_').Replace('+', '_').Replace('.', '_');

            // We require the following from our Target Framework Define names
            // Starts with A-Z or _
            // Contains only A-Z, 0-9 and _
            if (!string.IsNullOrEmpty(candidateName) &&
                (char.IsLetter(candidateName[0]) || candidateName[0] == '_') &&
                candidateName.All(c => Char.IsLetterOrDigit(c) || c == '_'))
            {
                return candidateName;
            }

            return null;
        }
    }
}
