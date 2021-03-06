// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

 using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ApiGenerator.Configuration;
using ApiGenerator.Domain;
using ApiGenerator.Domain.Specification;
using ApiGenerator.Generator.Razor;
using Newtonsoft.Json.Linq;
using ShellProgressBar;

namespace ApiGenerator.Generator
{
	public class ApiGenerator
	{
		public static List<string> Warnings { get; private set; } = new List<string>();

		public static async Task Generate(string downloadBranch, bool lowLevelOnly, RestApiSpec spec)
		{
			async Task Generate(IList<RazorGeneratorBase> generators, RestApiSpec restApiSpec, bool highLevel)
			{
				var pbarOpts = new ProgressBarOptions { BackgroundColor = ConsoleColor.DarkGray };
				var message = $"Generating {(highLevel ? "high" : "low")} level code";
				using var pbar = new ProgressBar(generators.Count, message, pbarOpts);
				foreach (var generator in generators)
				{
					pbar.Message = "Generating " + generator.Title;
					await generator.Generate(restApiSpec, pbar);
					pbar.Tick("Generated " + generator.Title);
				}
			}


			var lowLevelGenerators = new List<RazorGeneratorBase>
			{
				//low level client
				new LowLevelClientInterfaceGenerator(),
				new LowLevelClientImplementationGenerator(),
				new RequestParametersGenerator(),
				new EnumsGenerator(),
				new ApiUrlsLookupsGenerator(),
			};

			var highLevelGenerators = new List<RazorGeneratorBase>
			{
				//high level client
				new HighLevelClientInterfaceGenerator(),
				new HighLevelClientImplementationGenerator(),
				new DescriptorsGenerator(),
				new RequestsGenerator(),
			};

			await Generate(lowLevelGenerators, spec, highLevel: false);
			if (!lowLevelOnly)
				await Generate(highLevelGenerators, spec, highLevel: true);

			// Check if there are any non-Stable endpoints present.
			foreach (var endpoint in spec.Endpoints)
			{
				if (endpoint.Value.Stability != Stability.Stable)
				{
					Warnings.Add($"Endpoint {endpoint.Value.Name} is not marked as Stable ({endpoint.Value.Stability})");
				}
			}

			if (Warnings.Count == 0) return;

			Console.ForegroundColor = ConsoleColor.Yellow;
			foreach (var warning in Warnings.Distinct().OrderBy(w => w))
				Console.WriteLine(warning);
			Console.ResetColor();
		}

		public static RestApiSpec CreateRestApiSpecModel(string downloadBranch, params string[] folders)
		{
			var directories = Directory.GetDirectories(GeneratorLocations.RestSpecificationFolder, "*", SearchOption.AllDirectories)
				.Where(f => folders == null || folders.Length == 0 || folders.Contains(new DirectoryInfo(f).Name))
				.OrderBy(f => new FileInfo(f).Name)
				.ToList();

			var endpoints = new SortedDictionary<string, ApiEndpoint>();
			var seenFiles = new HashSet<string>();
			using (var pbar = new ProgressBar(directories.Count, $"Listing {directories.Count} directories",
				new ProgressBarOptions { BackgroundColor = ConsoleColor.DarkGray, CollapseWhenFinished = false }))
			{
				var folderFiles = directories.Select(dir =>
					Directory.GetFiles(dir)
						.Where(f => f.EndsWith(".json") && !CodeConfiguration.IgnoredApis.Contains(new FileInfo(f).Name))
						.ToList()
				);
				var commonFile = Path.Combine(GeneratorLocations.RestSpecificationFolder, "Core", "_common.json");
				if (!File.Exists(commonFile)) throw new Exception($"Expected to find {commonFile}");

				RestApiSpec.CommonApiQueryParameters = CreateCommonApiQueryParameters(commonFile);

				foreach (var jsonFiles in folderFiles)
				{
					using (var fileProgress = pbar.Spawn(jsonFiles.Count, $"Listing {jsonFiles.Count} files",
						new ProgressBarOptions { ProgressCharacter = '─', BackgroundColor = ConsoleColor.DarkGray }))
					{
						foreach (var file in jsonFiles)
						{
							if (file.EndsWith("_common.json")) continue;
							else if (file.EndsWith(".patch.json")) continue;
							else
							{
								var endpoint = ApiEndpointFactory.FromFile(file);
								seenFiles.Add(Path.GetFileNameWithoutExtension(file));
								endpoints.Add(endpoint.Name, endpoint);
							}

							fileProgress.Tick();
						}
					}
					pbar.Tick();
				}
			}
			var wrongMapsApi = CodeConfiguration.ApiNameMapping.Where(k => !string.IsNullOrWhiteSpace(k.Key) && !seenFiles.Contains(k.Key));
			foreach (var (key, value) in wrongMapsApi)
			{
				var isIgnored = CodeConfiguration.IgnoredApis.Contains($"{value}.json");
				if (isIgnored)
					Warnings.Add($"{value} uses MapsApi: {key} ignored in ${nameof(CodeConfiguration)}.{nameof(CodeConfiguration.IgnoredApis)}");
				else Warnings.Add($"{value} uses MapsApi: {key} which does not exist");
			}

			return new RestApiSpec { Endpoints = endpoints, Commit = downloadBranch };
		}

		private static SortedDictionary<string, QueryParameters> CreateCommonApiQueryParameters(string jsonFile)
		{
			var json = File.ReadAllText(jsonFile);
			var jobject = JObject.Parse(json);
			var commonParameters = jobject.Property("params").Value.ToObject<Dictionary<string, QueryParameters>>();
			return ApiQueryParametersPatcher.Patch(null, commonParameters, null, false);
		}
	}
}
