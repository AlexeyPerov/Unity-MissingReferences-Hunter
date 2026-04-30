// #define LOGS

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// ReSharper disable StringLiteralTypo
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable MemberCanBePrivate.Global


// ReSharper disable once CheckNamespace
namespace MissingReferencesHunter
{
    public class MissingReferencesWindow : EditorWindow
    {
        private class Result
        {
            public List<AssetData> Assets { get; } = new List<AssetData>();
            public HashSet<string> Guids { get; } = new HashSet<string>();
            public HashSet<long> FileIDs { get; } = new HashSet<long>();
            public string OutputDescription { get; set; }

            public Dictionary<string, int> FieldTypeCounters { get; } = new Dictionary<string, int>();
            public int FieldTypeSum { get; set; }

            public int MissingFileIDAndGuidCases { get; set; }
            public int MissingFileIDCases { get; set; }
            public int MissingGuidCases { get; set; }
            public int MissingLocalFileIDCases { get; set; }
            public int EmptyFileIDCases { get; set; }

            public int MissingMethodCases { get; set; }
            public int TypeMismatchCases { get; set; }

            public int MissingScriptCases { get; set; }
            public int DuplicateComponentCases { get; set; }
            public int InvalidLayerCases { get; set; }
        }

        private class OutputSettings
        {
            public const int PageSize = 35;

            public int? PageToShow { get; set; }
            
            public string PathFilter { get; set; }
            
            public bool ShowMissingFileIDAndGuid { get; set; } = true;
            public bool ShowMissingGuid { get; set; } = true;
            public bool ShowMissingFileID { get; set; }
            public bool ShowMissingLocalFileID { get; set; }
            public bool ShowEmptyLocalRefs { get; set; }
            public bool ShowFileIDIssues { get; set; }

            public bool ShowMissingMethods { get; set; } = true;
            public bool ShowTypeMismatches { get; set; } = true;

            public bool ShowMissingScripts { get; set; } = true;
            public bool ShowDuplicateComponents { get; set; } = true;
            public bool ShowInvalidLayers { get; set; } = true;

            public HashSet<string> FieldTypesToShow { get; } = new HashSet<string>();
        }
        
        private Result _result;
        private OutputSettings _outputSettings;

        private bool _analysisOngoing;

        private bool _infoFoldout;
        private bool _settingsFoldout;
        
        private bool _enableMissingMethodScan = true;
        private bool _enableTypeMismatchScan = true;
        private bool _enableMissingScriptScan = true;
        private bool _enableDuplicateComponentScan = true;
        private bool _enableInvalidLayerScan = true;
        
        private Vector2 _pagesScroll = Vector2.zero;
        private Vector2 _fieldTypesScroll = Vector2.zero;
        private Vector2 _assetsScroll = Vector2.zero;

        private readonly List<string> _keyWordsToIgnore = new List<string>
        {
            "objectReference: {fileID:",
            "m_CorrespondingSourceObject: {fileID:",
            "m_PrefabInstance: {fileID:",
            "m_PrefabAsset: {fileID:",
            "m_GameObject: {fileID:",
            "m_Icon: {fileID:",
            "m_Father: {fileID:"
        };

        private readonly List<string> _keyWordsToIgnoreInSceneAsset = new List<string>
        {
            "m_OcclusionCullingData: {fileID:",
            "m_HaloTexture: {fileID:",
            "m_CustomReflection: {fileID:",
            "m_Sun: {fileID:",
            "m_LightmapParameters: {fileID:",
            "m_LightingDataAsset: {fileID:",
            "m_LightingSettings: {fileID:",
            "m_NavMeshData: {fileID:",
            "m_Icon: {fileID:",
            "m_StaticBatchRoot: {fileID:",
            "m_ProbeAnchor: {fileID:",
            "m_LightProbeVolumeOverride: {fileID:",
            "m_Cookie: {fileID:",
            "m_Flare: {fileID:",
            "m_TargetTexture: {fileID:",
        };

        private readonly List<string> _defaultIgnorePatterns = new List<string>
        {
            @"ProjectSettings/",
            @"Packages/",
            @"\.asmdef$",
            @"link\.xml$",
            @"\.csv$",
            @"\.md$",
            @"\.json$",
            @"\.xml$",
            @"\.txt$"
        };
        
        [MenuItem("Tools/Missing References Hunter")]
        public static void LaunchUnreferencedAssetsWindow()
        {
            GetWindow<MissingReferencesWindow>("Missing References");
        }
        
        private void OnDestroy()
        {
            Clear();
        }
        
        private static void Clear()
        {
            EditorUtility.UnloadUnusedAssetsImmediate();
        }

        private IEnumerator PopulateMissingReferencesList()
        {
            _analysisOngoing = true;
            
            _result = new Result();
            
            _outputSettings = new OutputSettings
            {
                PageToShow = 0
            };

            Clear();
            Show();

            var assetPaths = AssetDatabase.GetAllAssetPaths().ToList();

            HashSet<int> validLayers = null;
            if (_enableInvalidLayerScan)
            {
                validLayers = LoadValidLayers();
            }

            EditorUtility.ClearProgressBar();
            
            var regexFileAndGuid = new Regex(@"fileID: \d+, guid: [a-f0-9]" + "{" + "32" + "}");
            var regexFileID = new Regex(@"{fileID: \d+}");
            var regexTypeStart = new Regex(@"^[a-zA-Z0-9_ ]+:");
            
            var count = 0;
            for (var assetIndex = 0; assetIndex < assetPaths.Count; assetIndex++)
            {
                if (assetIndex % 20000 == 0)
                {
                    GC.Collect();
                    yield return 0.05f;
                    GC.Collect();
                }
                
                var assetPath = assetPaths[assetIndex];
                EditorUtility.DisplayProgressBar("Missing References", "Searching for missing references",
                    (float)count / assetPaths.Count);
                count++;

                var assetObject = AssetDatabase.LoadAssetAtPath<Object>(assetPath);

                if (assetObject == null)
                {
                    Debug.LogWarning($"Unable to load an asset at path: {assetPath}");
                    continue;
                }

                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(assetObject, out var guidStr, out long fileId))
                {
                    Debug.LogWarning($"Unable to load guid and local id at path: {assetPath}");
                    continue;
                }

                _result.Guids.Add(guidStr);
                _result.FileIDs.Add(fileId);
                
                var isIncluded = IsIncludedInAnalysis(assetPath);

                if (!isIncluded)
                    continue;

                var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                var validAssetType = IsValidType(assetPath, type);

                if (!validAssetType)
                    continue;

                if (!CanAnalyzeType(type))
                    continue;

                var typeName = GetReadableTypeName(type);

                var lines = TryReadAsLines(assetPath);

#if LOGS
                Debug.LogWarning("[Analyzing: " + assetPath + " Lines: " + lines.Length + "]");
#endif

                var refsData = new AssetReferencesData();

                if (lines.Length > 0)
                {
                    for (var index = 0; index < lines.Length; index++)
                    {
                        var lineOriginal = lines[index];
                        var line = lineOriginal;

                        var systemLine = false;

                        foreach (var keyword in _keyWordsToIgnore)
                        {
                            if (line.Contains(keyword))
                            {
                                systemLine = true;
                                break;
                            }
                        }

                        if (systemLine)
                        {
                            continue;
                        }

                        if (type == typeof(SceneAsset))
                        {
                            foreach (var keyword in _keyWordsToIgnoreInSceneAsset)
                            {
                                if (line.Contains(keyword))
                                {
                                    systemLine = true;
                                    break;
                                }
                            }
                        }

                        if (systemLine)
                        {
                            continue;
                        }

                        if (line.Contains("guid:"))
                        {
                            var guidMatches = regexFileAndGuid.Matches(line);

                            for (var i = 0; i < guidMatches.Count; i++)
                            {
                                var match = guidMatches[i];
                                var str = match.Value;

                                var localIdPart = str.Substring(7, str.IndexOf(",", StringComparison.Ordinal) - 7);
                                localIdPart = localIdPart.Trim();

                                if (!long.TryParse(localIdPart, out var localFileID))
                                {
                                    Debug.LogWarning($"Unable to parse local id {localIdPart}");
                                }

                                var externalGuid = str.Substring(str.Length - 32);
                                var guidValid = !externalGuid.StartsWith("0000000000");
                                var localIdValid = localFileID > 0;

                                if (!localIdValid && !guidValid)
                                {
                                    Debug.LogWarning($"Both local id and guid are invalid at {str}");
                                    continue;
                                }

                                var referenceData = new ExternalReferenceRegistry(localIdValid, guidValid, localFileID,
                                    externalGuid, index);
                                refsData.ExternalReferences.Add(referenceData);

                                var extendedPlaceDataRecorded = false;

                                if (guidValid)
                                {
                                    var existsInAssets = _result.Guids.Contains(externalGuid) ||
                                                         !string.IsNullOrEmpty(
                                                             AssetDatabase.GUIDToAssetPath(externalGuid));

                                    referenceData.GuidExistsInAssets = existsInAssets;

                                    if (!existsInAssets)
                                    {
                                        RecordGuidPlaceData(index, lines, referenceData);
                                        extendedPlaceDataRecorded = true;
                                    }
                                }

                                if (!extendedPlaceDataRecorded)
                                {
                                    referenceData.Sample.Add(lines[index]);
                                }

                                FindFieldType(regexTypeStart, index, lines, referenceData);
                            }
                        }
                        else if (line.Contains("fileID:"))
                        {
                            var fileIdMatches = regexFileID.Matches(line);

                            for (var i = 0; i < fileIdMatches.Count; i++)
                            {
                                var match = fileIdMatches[i];
                                var str = match.Value;

                                str = str.Substring(9);
                                str = str.Replace("}", string.Empty);

                                var localFileID = str;

#if LOGS
                                Debug.Log($"Found FileID: {localFileID} [Length: {localFileID.Length}] at {index}");
#endif

                                if (localFileID == "0")
                                {
                                    refsData.EmptyFileIDs.Add(new EmptyLocalFileIDRegistry(index));
                                }
                                else
                                {
                                    if (long.TryParse(localFileID, out var id))
                                    {
                                        refsData.LocalReferences.Add(new LocalReferenceRegistry(id, index));
                                    }
                                    else
                                    {
                                        Debug.LogWarning($"Unable to parse local id {localFileID}");
                                    }
                                }
                            }
                        }
                    }
                }

                foreach (var localId in refsData.LocalReferences)
                {
                    for (var index = 0; index < lines.Length; index++)
                    {
                        var line = lines[index];
                        if (index != localId.Line && line.Contains(localId.IdStr))
                        {
                            localId.LocalUsagesCount++;
                        }
                    }
                }

                if (_enableMissingMethodScan || _enableTypeMismatchScan)
                {
                    ScanUnityEventReferences(lines, refsData, _enableMissingMethodScan, _enableTypeMismatchScan);
                }

                if (_enableMissingScriptScan)
                {
                    ScanMissingScripts(lines, refsData);
                }

                if (_enableInvalidLayerScan && validLayers != null)
                {
                    ScanInvalidLayers(lines, refsData, validLayers);
                }

                if (_enableDuplicateComponentScan && type == typeof(GameObject))
                {
                    ScanDuplicateComponents(assetPath, refsData);
                }

                _result.Assets.Add(new AssetData(assetPath, type, typeName, guidStr, refsData));
            }
            
            GC.Collect();
            yield return 0.05f;
            GC.Collect();

            foreach (var asset in _result.Assets)
            {
                foreach (var registry in asset.RefsData.LocalReferences)
                {
                    registry.ExistsInAssets = _result.FileIDs.Contains(registry.Id);
                }

                foreach (var registry in asset.RefsData.ExternalReferences)
                {
                    if (registry.FileIDValid)
                    {
                        registry.FileIDExistsInAssets = _result.FileIDs.Contains(registry.FileID)
                                                        || asset.RefsData.LocalReferences.Any(x => x.Id == registry.FileID);
                    }
                }
                
                asset.RefsData.CalculateCounters();

                foreach (var registry in asset.RefsData.ExternalReferences)
                {
                    if (!string.IsNullOrWhiteSpace(registry.FieldType) && registry.WarningLevel > 0)
                    {
                        asset.MissingFieldTypes.Add(registry.FieldType);
                        
                        if (!_result.FieldTypeCounters.ContainsKey(registry.FieldType))
                            _result.FieldTypeCounters[registry.FieldType] = 1;
                        else
                            _result.FieldTypeCounters[registry.FieldType] += 1;

                        _result.FieldTypeSum++;
                    }
                }
            }

            _result.MissingFileIDAndGuidCases = _result.Assets.Count(x => x.RefsData.MissingFileIDAndGuid > 0);
            _result.MissingGuidCases = _result.Assets.Count(x => x.RefsData.MissingGuid > 0);
            _result.MissingFileIDCases = _result.Assets.Count(x => x.RefsData.MissingFileID > 0);
            _result.MissingLocalFileIDCases = _result.Assets.Count(x => x.RefsData.MissingLocalFileID > 0);
            _result.EmptyFileIDCases = _result.Assets.Count(x => x.RefsData.EmptyFileIDs.Count > 0);
            _result.MissingMethodCases = _result.Assets.Count(x => x.RefsData.MissingMethods.Count > 0);
            _result.TypeMismatchCases = _result.Assets.Count(x => x.RefsData.TypeMismatches.Count > 0);
            _result.MissingScriptCases = _result.Assets.Count(x => x.RefsData.MissingScripts.Count > 0);
            _result.DuplicateComponentCases = _result.Assets.Count(x => x.RefsData.DuplicateComponents.Count > 0);
            _result.InvalidLayerCases = _result.Assets.Count(x => x.RefsData.InvalidLayers.Count > 0);
            
            var casesWithNoWarnings = _result.Assets.Count(x => !x.RefsData.HasWarnings);
            var assetsWithWarnings = _result.Assets.Count - casesWithNoWarnings;
            
            _result.OutputDescription = $"Assets with issues: {assetsWithWarnings}";
            if (_enableMissingMethodScan)
                _result.OutputDescription += $" | Missing Methods: {_result.MissingMethodCases}";
            if (_enableTypeMismatchScan)
                _result.OutputDescription += $" | Type Mismatches: {_result.TypeMismatchCases}";
            if (_enableMissingScriptScan)
                _result.OutputDescription += $" | Missing Scripts: {_result.MissingScriptCases}";
            if (_enableDuplicateComponentScan)
                _result.OutputDescription += $" | Dup Components: {_result.DuplicateComponentCases}";
            if (_enableInvalidLayerScan)
                _result.OutputDescription += $" | Invalid Layers: {_result.InvalidLayerCases}";

            EditorUtility.ClearProgressBar();
            
            Debug.Log(_result.OutputDescription);

            _analysisOngoing = false;

            string[] TryReadAsLines(string path)
            {
                if (Directory.Exists(path))
                    return Array.Empty<string>();

                try
                {
                    return File.ReadAllLines(path);
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                    return Array.Empty<string>();
                }
            }
            
            string GetReadableTypeName(Type type)
            {
                string typeName;
            
                if (type != null)
                {
                    typeName = type.ToString();
                    typeName = typeName.Replace("UnityEngine.", string.Empty);
                    typeName = typeName.Replace("UnityEditor.", string.Empty);
                }
                else
                {
                    typeName = "Missing Type";
                }

                return typeName;
            }
            
            bool IsIncludedInAnalysis(string path)
            {
                var notExcluded = _defaultIgnorePatterns.All(pattern 
                    => string.IsNullOrEmpty(pattern) || !Regex.Match(path, pattern).Success);

                return notExcluded;
            }
            
            bool IsValidType(string path, Type type)
            {
                if (type != null)
                {
                    if (type == typeof(DefaultAsset)) // DefaultAsset goes for e.g. folders.
                    {
                        return false;
                    }
                    
                    return true;
                }
                Debug.LogWarning($"Invalid asset type found at {path}");
                return false;
            }

            bool CanAnalyzeType(Type type)
            {
                return type == typeof(GameObject) || type == typeof(SceneAsset) 
                                                  || DerivesFromOrEqual(type, typeof(ScriptableObject));
            }
            
            static bool DerivesFromOrEqual(Type a, Type b)
            {
#if UNITY_WSA && ENABLE_DOTNET && !UNITY_EDITOR
                return b == a || b.GetTypeInfo().IsAssignableFrom(a.GetTypeInfo());
#else
                return b == a || b.IsAssignableFrom(a);
#endif
            }
        }

        private static void FindFieldType(Regex regexTypeStart, int index, string[] lines, ExternalReferenceRegistry referenceData)
        {
            for (var j = index; j >= 0; j--)
            {
                var match = regexTypeStart.Match(lines[j]);
                if (match.Success)
                {
                    var typeValue = match.Value.Replace(":", string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(typeValue))
                    {
                        referenceData.FieldType = typeValue;
                        break;
                    }
                }
            }
        }
        
        private static void RecordGuidPlaceData(int index, string[] lines, ExternalReferenceRegistry referenceData)
        {
            for (var j = index - 4; j < index + 5; j++)
            {
                if (j >= 0 && j < lines.Length)
                {
                    referenceData.Sample.Add(lines[j]);
                }
            }

            var holderName = string.Empty;
            const string nameTag = "m_Name:";
                                        
            for (var j = index; j >= 0; j--)
            {
                if (lines[j].Contains(nameTag))
                {
                    var nameCandidate = lines[j].Replace(nameTag, string.Empty).Trim();

                    if (!string.IsNullOrWhiteSpace(nameCandidate))
                    {
                        holderName = nameCandidate;
                        break;
                    }
                }
            }

            referenceData.HolderName = holderName;
        }

        private static readonly Regex TargetAssemblyTypeRegex = new Regex(@"m_TargetAssemblyTypeName:\s*([\w.]+)");
        private static readonly Regex MethodNameRegex = new Regex(@"m_MethodName:\s*(\w+)");
        private static readonly Regex ArgAssemblyTypeRegex = new Regex(@"m_ObjectArgumentAssemblyTypeName:\s*([\w.]+)");

        private static void ScanUnityEventReferences(string[] lines, AssetReferencesData refsData,
            bool checkMissingMethods, bool checkTypeMismatches)
        {
            var targetTypes = new List<(string typeName, int line)>();
            var methodNames = new List<(string methodName, int line)>();
            var argTypes = new List<(string typeName, int line)>();

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (checkMissingMethods)
                {
                    var targetMatch = TargetAssemblyTypeRegex.Match(line);
                    if (targetMatch.Success)
                    {
                        targetTypes.Add((targetMatch.Groups[1].Value, i));
                    }

                    var methodMatch = MethodNameRegex.Match(line);
                    if (methodMatch.Success)
                    {
                        methodNames.Add((methodMatch.Groups[1].Value, i));
                    }
                }

                if (checkTypeMismatches)
                {
                    var argMatch = ArgAssemblyTypeRegex.Match(line);
                    if (argMatch.Success)
                    {
                        argTypes.Add((argMatch.Groups[1].Value, i));
                    }
                }
            }

            if (checkMissingMethods)
            {
                var pairCount = Math.Min(targetTypes.Count, methodNames.Count);
                for (var i = 0; i < pairCount; i++)
                {
                    var (className, _) = targetTypes[i];
                    var (methodName, methodLine) = methodNames[i];

                    if (string.IsNullOrEmpty(className))
                        continue;

                    var targetType = ResolveType(className);
                    if (targetType == null)
                        continue;

                    var methods = targetType.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static);
                    var methodExists = methods.Any(m => m.Name == methodName);

                    if (!methodExists)
                    {
                        refsData.MissingMethods.Add(new MissingMethodEntry(className, methodName, methodLine));
                    }
                }
            }

            if (checkTypeMismatches)
            {
                foreach (var (typeName, line) in argTypes)
                {
                    if (string.IsNullOrEmpty(typeName))
                        continue;

                    var resolvedType = ResolveType(typeName);
                    if (resolvedType == null)
                    {
                        refsData.TypeMismatches.Add(new TypeMismatchEntry(typeName, line));
                    }
                }
            }
        }

        private static Type ResolveType(string typeName)
        {
            var cleanName = typeName.Split(',')[0].Trim();

            var type = Type.GetType(cleanName);
            if (type != null) return type;

            type = Type.GetType($"{cleanName}, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            if (type != null) return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(cleanName);
                    if (type != null) return type;
                }
                catch
                {
                    // skip assemblies that throw on GetType
                }
            }

            return null;
        }

        private static readonly Regex ScriptGuidRegex = new Regex(@"m_Script:\s*\{fileID:\s*\d+,\s*guid:\s*([a-f0-9]{32})");
        private static readonly Regex LayerRegex = new Regex(@"^\s*m_Layer:\s*(\d+)\s*$");

        private static void ScanMissingScripts(string[] lines, AssetReferencesData refsData)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var match = ScriptGuidRegex.Match(lines[i]);
                if (match.Success)
                {
                    var guid = match.Groups[1].Value;
                    if (guid.StartsWith("0000000000"))
                        continue;

                    var scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(scriptPath))
                    {
                        refsData.MissingScripts.Add(new MissingScriptEntry(guid, i));
                    }
                }
            }
        }

        private static void ScanInvalidLayers(string[] lines, AssetReferencesData refsData, HashSet<int> validLayers)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var match = LayerRegex.Match(lines[i]);
                if (match.Success)
                {
                    var layerIndex = int.Parse(match.Groups[1].Value);
                    if (!validLayers.Contains(layerIndex))
                    {
                        refsData.InvalidLayers.Add(new InvalidLayerEntry(layerIndex, i));
                    }
                }
            }
        }

        private static HashSet<int> LoadValidLayers()
        {
            var validLayers = new HashSet<int>();
            var tagManagerPath = "ProjectSettings/TagManager.asset";

            if (!File.Exists(tagManagerPath))
                return validLayers;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(tagManagerPath);
            }
            catch
            {
                return validLayers;
            }

            var inLayers = false;
            var layerIndex = 0;

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();

                if (!inLayers)
                {
                    if (trimmed.StartsWith("layers:"))
                    {
                        inLayers = true;
                    }
                    continue;
                }

                if (layerIndex >= 32)
                    break;

                if (trimmed.StartsWith("- "))
                {
                    var name = trimmed.Substring(2).Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        validLayers.Add(layerIndex);
                    }
                    layerIndex++;
                }
                else if (!line.StartsWith(" ") && !line.StartsWith("\t"))
                {
                    break;
                }
            }

            return validLayers;
        }

        private static void ScanDuplicateComponents(string assetPath, AssetReferencesData refsData)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go == null) return;

            var allTransforms = go.GetComponentsInChildren<Transform>(true);

            foreach (var t in allTransforms)
            {
                var childGo = t.gameObject;
                var components = childGo.GetComponents<Component>();
                var typeCounts = new Dictionary<Type, int>();

                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    var type = comp.GetType();
                    if (!typeCounts.ContainsKey(type))
                        typeCounts[type] = 0;
                    typeCounts[type]++;
                }

                foreach (var kvp in typeCounts)
                {
                    if (kvp.Value > 1)
                    {
                        refsData.DuplicateComponents.Add(new DuplicateComponentEntry(
                            kvp.Key.Name, kvp.Value, childGo.name));
                    }
                }
            }
        }
        
        private void OnGUI()
        {
            GUIUtilities.HorizontalLine();

            DrawAnalysisControlSection();
            
            GUIUtilities.HorizontalLine();

            DrawInfoSection();
            
            GUIUtilities.HorizontalLine();

            if (_result == null || _analysisOngoing)
            {
                return;
            }
            
            if (_result.Assets.Count == 0)
            {
                EditorGUILayout.LabelField("No missing references found");
                return;
            }
            
            var assets = _result.Assets;
            
            if (!string.IsNullOrEmpty(_outputSettings.PathFilter))
            {
                assets = assets.Where(x => x.Path.Contains(_outputSettings.PathFilter)).ToList();
            }

            var filteredAssets = new List<AssetData>();

            foreach (var asset in assets)
            {
                if (_outputSettings.FieldTypesToShow.Count > 0)
                {
                    if (!_outputSettings.FieldTypesToShow.Any(x => asset.MissingFieldTypes.Contains(x)))
                    { 
                        continue;
                    }
                }
                
                if (_outputSettings.ShowMissingFileIDAndGuid && asset.RefsData.MissingFileIDAndGuid > 0)
                {
                    filteredAssets.Add(asset);
                    continue;
                }
                
                if (_outputSettings.ShowMissingFileID && asset.RefsData.MissingFileID > 0)
                {
                    filteredAssets.Add(asset);
                    continue;
                }
                
                if (_outputSettings.ShowMissingGuid && asset.RefsData.MissingGuid > 0)
                {
                    filteredAssets.Add(asset);
                    continue;
                }
                
                if (_outputSettings.ShowMissingLocalFileID && asset.RefsData.MissingLocalFileID > 0)
                {
                    filteredAssets.Add(asset);
                    continue;
                }
                
                if (_outputSettings.ShowEmptyLocalRefs && asset.RefsData.EmptyFileIDs.Count > 0)
                {
                    filteredAssets.Add(asset);
                    continue;
                }

                if (_outputSettings.ShowMissingMethods && asset.RefsData.MissingMethods.Count > 0)
                {
                    filteredAssets.Add(asset);
                    continue;
                }

                if (_outputSettings.ShowTypeMismatches && asset.RefsData.TypeMismatches.Count > 0)
                {
                    filteredAssets.Add(asset);
                    continue;
                }

                if (_outputSettings.ShowMissingScripts && asset.RefsData.MissingScripts.Count > 0)
                {
                    filteredAssets.Add(asset);
                    continue;
                }

                if (_outputSettings.ShowDuplicateComponents && asset.RefsData.DuplicateComponents.Count > 0)
                {
                    filteredAssets.Add(asset);
                    continue;
                }

                if (_outputSettings.ShowInvalidLayers && asset.RefsData.InvalidLayers.Count > 0)
                {
                    filteredAssets.Add(asset);
                }
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(_result.OutputDescription);

            EditorGUILayout.EndHorizontal();

            _pagesScroll = EditorGUILayout.BeginScrollView(_pagesScroll);

            EditorGUILayout.BeginHorizontal();
            
            var totalCount = filteredAssets.Count;
            var pagesCount = totalCount / OutputSettings.PageSize + (totalCount % OutputSettings.PageSize > 0 ? 1 : 0);
            var showAllButton = totalCount <= 150;
            if (!showAllButton && !_outputSettings.PageToShow.HasValue && pagesCount > 0)
            {
                _outputSettings.PageToShow = 0;
            }

            var prevColor = GUI.color;
            if (showAllButton)
            {
                GUI.color = !_outputSettings.PageToShow.HasValue ? Color.yellow : Color.white;

                if (GUILayout.Button("All", GUILayout.Width(30f)))
                {
                    _outputSettings.PageToShow = null;
                }

                GUI.color = prevColor;
            }

            for (var i = 0; i < pagesCount; i++)
            {
                prevColor = GUI.color;
                GUI.color = _outputSettings.PageToShow == i ? Color.yellow : Color.white;

                if (GUILayout.Button((i + 1).ToString(), GUILayout.Width(30f)))
                {
                    _outputSettings.PageToShow = i;
                }

                GUI.color = prevColor;
            }

            if (_outputSettings.PageToShow.HasValue && _outputSettings.PageToShow > pagesCount - 1)
            {
                _outputSettings.PageToShow = pagesCount - 1;
            }

            if (_outputSettings.PageToShow.HasValue && pagesCount == 0)
            {
                _outputSettings.PageToShow = null;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
            
            GUIUtilities.HorizontalLine();
        
            EditorGUILayout.BeginHorizontal();

            var textFieldStyle = EditorStyles.textField;
            var prevTextFieldAlignment = textFieldStyle.alignment;
            textFieldStyle.alignment = TextAnchor.MiddleCenter;
            
            _outputSettings.PathFilter = EditorGUILayout.TextField("Path Contains:", 
                _outputSettings.PathFilter, GUILayout.Width(400f));
            
            EditorGUILayout.EndHorizontal();
            
            GUIUtilities.HorizontalLine();
            
            EditorGUILayout.LabelField("Filter by Type:");
            
            _fieldTypesScroll = GUILayout.BeginScrollView(_fieldTypesScroll);

            EditorGUILayout.BeginHorizontal();
            
            prevColor = GUI.color;

            var allSelected = _outputSettings.FieldTypesToShow.Count == 0 ||
                              _outputSettings.FieldTypesToShow.Count == _result.FieldTypeCounters.Count;

            GUI.color = allSelected ? Color.cyan : Color.white;
            
            if (GUILayout.Button($"All [{_result.FieldTypeSum}]"))
            {
                if (allSelected)
                    _outputSettings.FieldTypesToShow.Clear();
                else
                {
                    foreach (var (key, _) in _result.FieldTypeCounters)
                    {
                        _outputSettings.FieldTypesToShow.Add(key);
                    }
                }
            }

            GUI.color = prevColor;
            
            foreach (var pair in _result.FieldTypeCounters)
            {
                prevColor = GUI.color;
                
                var selected = _outputSettings.FieldTypesToShow.Contains(pair.Key);

                GUI.color = selected ? Color.cyan : Color.white;
                
                if (GUILayout.Button($"{pair.Key}"))
                {
                    if (_outputSettings.FieldTypesToShow.Contains(pair.Key))
                        _outputSettings.FieldTypesToShow.Remove(pair.Key);
                    else
                        _outputSettings.FieldTypesToShow.Add(pair.Key);
                }
                
                GUI.color = prevColor;
            }
            
            EditorGUILayout.EndHorizontal();
            
            GUILayout.EndScrollView();
            
            GUIUtilities.HorizontalLine();
            
            EditorGUILayout.BeginHorizontal();
            
            var assetsWithMissingExternalRefs = $"Missing both FileID and Guid [{_result.MissingFileIDAndGuidCases}]: ";
            GUI.color = _outputSettings.ShowMissingFileIDAndGuid ? Color.red : Color.gray;
            if (GUILayout.Button(assetsWithMissingExternalRefs + (_outputSettings.ShowMissingFileIDAndGuid ? "Shown" : "Hidden")))
            {
                _outputSettings.ShowMissingFileIDAndGuid = !_outputSettings.ShowMissingFileIDAndGuid;
            }
            
            var assetsWithMissingGuids = $"Missing Guid [{_result.MissingGuidCases}]: ";
            GUI.color = _outputSettings.ShowMissingGuid ? Color.yellow : Color.gray;
            if (GUILayout.Button(assetsWithMissingGuids + (_outputSettings.ShowMissingGuid ? "Shown" : "Hidden")))
            {
                _outputSettings.ShowMissingGuid = !_outputSettings.ShowMissingGuid;
            }
            
            EditorGUILayout.EndHorizontal();

            if (_enableMissingMethodScan || _enableTypeMismatchScan)
            {
                EditorGUILayout.BeginHorizontal();

                if (_enableMissingMethodScan)
                {
                    var assetsWithMissingMethods = $"<Missing> Methods [{_result.MissingMethodCases}]: ";
                    GUI.color = _outputSettings.ShowMissingMethods ? Color.magenta : Color.gray;
                    if (GUILayout.Button(assetsWithMissingMethods + (_outputSettings.ShowMissingMethods ? "Shown" : "Hidden")))
                    {
                        _outputSettings.ShowMissingMethods = !_outputSettings.ShowMissingMethods;
                    }
                }

                if (_enableTypeMismatchScan)
                {
                    var assetsWithTypeMismatches = $"Type Mismatch [{_result.TypeMismatchCases}]: ";
                    GUI.color = _outputSettings.ShowTypeMismatches ? new Color(1f, 0.5f, 0f) : Color.gray;
                    if (GUILayout.Button(assetsWithTypeMismatches + (_outputSettings.ShowTypeMismatches ? "Shown" : "Hidden")))
                    {
                        _outputSettings.ShowTypeMismatches = !_outputSettings.ShowTypeMismatches;
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            if (_enableMissingScriptScan || _enableDuplicateComponentScan || _enableInvalidLayerScan)
            {
                EditorGUILayout.BeginHorizontal();

                if (_enableMissingScriptScan)
                {
                    var assetsWithMissingScripts = $"Missing Scripts [{_result.MissingScriptCases}]: ";
                    GUI.color = _outputSettings.ShowMissingScripts ? Color.red : Color.gray;
                    if (GUILayout.Button(assetsWithMissingScripts + (_outputSettings.ShowMissingScripts ? "Shown" : "Hidden")))
                    {
                        _outputSettings.ShowMissingScripts = !_outputSettings.ShowMissingScripts;
                    }
                }

                if (_enableDuplicateComponentScan)
                {
                    var assetsWithDupComponents = $"Dup Components [{_result.DuplicateComponentCases}]: ";
                    GUI.color = _outputSettings.ShowDuplicateComponents ? Color.cyan : Color.gray;
                    if (GUILayout.Button(assetsWithDupComponents + (_outputSettings.ShowDuplicateComponents ? "Shown" : "Hidden")))
                    {
                        _outputSettings.ShowDuplicateComponents = !_outputSettings.ShowDuplicateComponents;
                    }
                }

                if (_enableInvalidLayerScan)
                {
                    var assetsWithInvalidLayers = $"Invalid Layers [{_result.InvalidLayerCases}]: ";
                    GUI.color = _outputSettings.ShowInvalidLayers ? Color.yellow : Color.gray;
                    if (GUILayout.Button(assetsWithInvalidLayers + (_outputSettings.ShowInvalidLayers ? "Shown" : "Hidden")))
                    {
                        _outputSettings.ShowInvalidLayers = !_outputSettings.ShowInvalidLayers;
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
            
            EditorGUILayout.BeginHorizontal();
            
            GUI.color = prevColor;

            _outputSettings.ShowFileIDIssues =
                EditorGUILayout.Toggle("Show FileID Issues:", _outputSettings.ShowFileIDIssues);
            
            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndHorizontal();

            if (_outputSettings.ShowFileIDIssues)
            {
                EditorGUILayout.BeginHorizontal();

                var assetsWithMissingFileID = $"Missing FileID [{_result.MissingFileIDCases}]: ";
                GUI.color = _outputSettings.ShowMissingFileID ? Color.cyan : Color.gray;
                if (GUILayout.Button(assetsWithMissingFileID +
                                     (_outputSettings.ShowMissingFileID ? "Shown" : "Hidden")))
                {
                    _outputSettings.ShowMissingFileID = !_outputSettings.ShowMissingFileID;
                }

                var assetWithMissingLocalFileID = $"Missing Local FileID [{_result.MissingLocalFileIDCases}]: ";
                GUI.color = _outputSettings.ShowMissingLocalFileID ? Color.cyan : Color.gray;
                if (GUILayout.Button(assetWithMissingLocalFileID +
                                     (_outputSettings.ShowMissingLocalFileID ? "Shown" : "Hidden")))
                {
                    _outputSettings.ShowMissingLocalFileID = !_outputSettings.ShowMissingLocalFileID;
                }

                var assetWithEmptyLocalFileID = $"Empty Local FileID [{_result.EmptyFileIDCases}]: ";
                GUI.color = _outputSettings.ShowEmptyLocalRefs ? Color.cyan : Color.gray;
                if (GUILayout.Button(assetWithEmptyLocalFileID +
                                     (_outputSettings.ShowEmptyLocalRefs ? "Shown" : "Hidden")))
                {
                    _outputSettings.ShowEmptyLocalRefs = !_outputSettings.ShowEmptyLocalRefs;
                }

                EditorGUILayout.EndHorizontal();
            }

            GUI.color = prevColor;
            
            textFieldStyle.alignment = prevTextFieldAlignment;
            
            GUIUtilities.HorizontalLine();
            
            _assetsScroll = GUILayout.BeginScrollView(_assetsScroll);
            
            EditorGUILayout.BeginVertical();

            for (var i = 0; i < filteredAssets.Count; i++)
            {
                if (_outputSettings.PageToShow.HasValue)
                {
                    var page = _outputSettings.PageToShow.Value;
                    if (i < page * OutputSettings.PageSize || i >= (page + 1) * OutputSettings.PageSize)
                    {
                        continue;
                    }
                }
                
                var asset = filteredAssets[i];
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(!asset.Foldout ? "Expand >>>" : "Colapse <<<", GUILayout.Width(100f)))
                {
                    asset.Foldout = !asset.Foldout;
                }
                
                EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(25f));
                
                prevColor = GUI.color;
                GUI.color = asset.ValidType ? Color.white : Color.red;
                EditorGUILayout.LabelField(asset.TypeName.Length > 16 
                    ? (asset.TypeName.Substring(0, 14) + "..") : asset.TypeName, GUILayout.Width(100f));    
                GUI.color = prevColor;
                
                if (asset.ValidType)
                {
                    var guiContent = EditorGUIUtility.ObjectContent(null, asset.Type);
                    guiContent.text = Path.GetFileName(asset.Path);

                    var alignment = GUI.skin.button.alignment;
                    GUI.skin.button.alignment = TextAnchor.MiddleLeft;

                    if (GUILayout.Button(guiContent, GUILayout.Width(280f), GUILayout.Height(18f)))
                    {
                        Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(asset.Path) };
                    }

                    GUI.skin.button.alignment = alignment;
                }
                
                prevColor = GUI.color;
                GUI.color = asset.RefsData.MissingFileIDAndGuid > 0 ? Color.red : Color.white;
                EditorGUILayout.LabelField("Missing FileID and Guid: " + asset.RefsData.MissingFileIDAndGuid, GUILayout.Width(160f));
                GUI.color = asset.RefsData.MissingGuid > 0 ? Color.yellow : Color.white;
                EditorGUILayout.LabelField("Missing Guid: " + asset.RefsData.MissingGuid, GUILayout.Width(120f));

                if (_enableMissingMethodScan)
                {
                    GUI.color = asset.RefsData.MissingMethods.Count > 0 ? Color.magenta : Color.gray;
                    EditorGUILayout.LabelField("Missing Methods: " + asset.RefsData.MissingMethods.Count, GUILayout.Width(130f));
                }

                if (_enableTypeMismatchScan)
                {
                    GUI.color = asset.RefsData.TypeMismatches.Count > 0 ? new Color(1f, 0.5f, 0f) : Color.gray;
                    EditorGUILayout.LabelField("Type Mismatch: " + asset.RefsData.TypeMismatches.Count, GUILayout.Width(130f));
                }

                if (_enableMissingScriptScan)
                {
                    GUI.color = asset.RefsData.MissingScripts.Count > 0 ? Color.red : Color.gray;
                    EditorGUILayout.LabelField("Miss Scripts: " + asset.RefsData.MissingScripts.Count, GUILayout.Width(110f));
                }

                if (_enableDuplicateComponentScan && asset.Type == typeof(GameObject))
                {
                    GUI.color = asset.RefsData.DuplicateComponents.Count > 0 ? Color.cyan : Color.gray;
                    EditorGUILayout.LabelField("Dup Comp: " + asset.RefsData.DuplicateComponents.Count, GUILayout.Width(100f));
                }

                if (_enableInvalidLayerScan)
                {
                    GUI.color = asset.RefsData.InvalidLayers.Count > 0 ? Color.yellow : Color.gray;
                    EditorGUILayout.LabelField("Inv Layers: " + asset.RefsData.InvalidLayers.Count, GUILayout.Width(110f));
                }

                if (_outputSettings.ShowFileIDIssues)
                {
                    GUI.color = asset.RefsData.MissingFileID > 0 ? Color.cyan : Color.white;
                    EditorGUILayout.LabelField("Missing FileID: " + asset.RefsData.MissingFileID,
                        GUILayout.Width(120f));
                    GUI.color = asset.RefsData.MissingLocalFileID > 0 ? Color.yellow : Color.white;
                    EditorGUILayout.LabelField("Missing Local FileID: " + asset.RefsData.MissingLocalFileID,
                        GUILayout.Width(140f));
                    GUI.color = asset.RefsData.EmptyFileIDs.Count > 0 ? Color.white : Color.gray;
                    EditorGUILayout.LabelField("Empty FileID: " + asset.RefsData.EmptyFileIDs.Count,
                        GUILayout.Width(140f));
                }
                
                GUI.color = prevColor;
                
                EditorGUILayout.EndHorizontal();


                if (asset.Foldout)
                {
                    GUIUtilities.HorizontalLine();

                    if (_outputSettings.ShowMissingFileIDAndGuid || _outputSettings.ShowMissingFileID ||
                        _outputSettings.ShowMissingGuid)
                    {
                        foreach (var registry in asset.RefsData.ExternalReferences.Where(x => x.WarningLevel > 0))
                        {
                            var fileIdIssue = registry.FileIDValid && !registry.FileIDExistsInAssets;
                            var guidIssue = registry.GuidValid && !registry.GuidExistsInAssets;
                            
                            if (!_outputSettings.ShowMissingFileID && !guidIssue)
                                continue;

                            if (fileIdIssue)
                            {
                                if (_outputSettings.ShowFileIDIssues || guidIssue)
                                {
                                    EditorGUILayout.BeginHorizontal();
                                    prevColor = GUI.color;
                                    GUI.color = Color.yellow;
                                    EditorGUILayout.LabelField($"Missing FileID {registry.FileID}");
                                    GUI.color = prevColor;
                                    if (GUILayout.Button("Copy"))
                                    {
                                        GUIUtility.systemCopyBuffer = registry.FileID.ToString();
                                    }

                                    GUILayout.FlexibleSpace();
                                    EditorGUILayout.EndHorizontal();
                                }
                                else
                                {
                                    continue;
                                }
                            }

                            if (guidIssue)
                            {
                                EditorGUILayout.BeginHorizontal();
                                prevColor = GUI.color;
                                GUI.color = Color.yellow;
                                EditorGUILayout.LabelField($"Missing GUID {registry.Guid}", GUILayout.Width(310f));
                                GUI.color = prevColor;
                                if (GUILayout.Button("Copy"))
                                {
                                    GUIUtility.systemCopyBuffer = registry.Guid;
                                }

                                GUILayout.FlexibleSpace();
                                EditorGUILayout.EndHorizontal();
                            }

                            var holderPostfix = string.Empty;

                            if (!string.IsNullOrEmpty(registry.HolderName))
                            {
                                holderPostfix = $"in [{registry.HolderName}] ";
                            }

                            var typePostfix = string.Empty;

                            if (!string.IsNullOrEmpty(registry.FieldType))
                            {
                                typePostfix = $" [{registry.FieldType}]";
                            }

                            EditorGUILayout.LabelField($"{holderPostfix}at line [{registry.Line + 1}]{typePostfix}");

                            prevColor = GUI.color;

                            foreach (var sampleLine in registry.Sample)
                            {
                                GUI.color = sampleLine.Contains(registry.Guid) ? Color.white : Color.gray;
                                EditorGUILayout.LabelField($"> {sampleLine}");
                            }

                            GUI.color = prevColor;

                            GUIUtilities.HorizontalLine();
                        }
                    }

                    if (_outputSettings.ShowEmptyLocalRefs || _outputSettings.ShowMissingLocalFileID)
                    {
                        foreach (var unknownLocalId in asset.RefsData.LocalReferences.Where(x =>
                                     x.LocalUsagesCount == 0 && !x.ExistsInAssets))
                        {
                            EditorGUILayout.LabelField(
                                $"Unknown FileID at [{unknownLocalId.Line + 1}] {unknownLocalId.Id}");
                            GUIUtilities.HorizontalLine();
                        }
                    }

                    if (_enableMissingMethodScan && _outputSettings.ShowMissingMethods && asset.RefsData.MissingMethods.Count > 0)
                    {
                        prevColor = GUI.color;
                        GUI.color = Color.magenta;
                        EditorGUILayout.LabelField("<Missing> Method References:");
                        GUI.color = prevColor;

                        foreach (var entry in asset.RefsData.MissingMethods)
                        {
                            EditorGUILayout.BeginHorizontal();
                            prevColor = GUI.color;
                            GUI.color = Color.magenta;
                            EditorGUILayout.LabelField($"  {entry.ClassName}.{entry.MethodName}", GUILayout.Width(400f));
                            GUI.color = prevColor;
                            EditorGUILayout.LabelField($"at line [{entry.Line + 1}]");
                            GUILayout.FlexibleSpace();
                            EditorGUILayout.EndHorizontal();
                        }

                        GUIUtilities.HorizontalLine();
                    }

                    if (_enableTypeMismatchScan && _outputSettings.ShowTypeMismatches && asset.RefsData.TypeMismatches.Count > 0)
                    {
                        prevColor = GUI.color;
                        GUI.color = new Color(1f, 0.5f, 0f);
                        EditorGUILayout.LabelField("Type Mismatch References:");
                        GUI.color = prevColor;

                        foreach (var entry in asset.RefsData.TypeMismatches)
                        {
                            EditorGUILayout.BeginHorizontal();
                            prevColor = GUI.color;
                            GUI.color = new Color(1f, 0.5f, 0f);
                            EditorGUILayout.LabelField($"  {entry.TypeName}", GUILayout.Width(400f));
                            GUI.color = prevColor;
                            EditorGUILayout.LabelField($"at line [{entry.Line + 1}]");
                            GUILayout.FlexibleSpace();
                            EditorGUILayout.EndHorizontal();
                        }

                        GUIUtilities.HorizontalLine();
                    }

                    if (_enableMissingScriptScan && _outputSettings.ShowMissingScripts && asset.RefsData.MissingScripts.Count > 0)
                    {
                        prevColor = GUI.color;
                        GUI.color = Color.red;
                        EditorGUILayout.LabelField("Missing Script References:");
                        GUI.color = prevColor;

                        foreach (var entry in asset.RefsData.MissingScripts)
                        {
                            EditorGUILayout.BeginHorizontal();
                            prevColor = GUI.color;
                            GUI.color = Color.red;
                            EditorGUILayout.LabelField($"  GUID: {entry.ScriptGuid}", GUILayout.Width(400f));
                            GUI.color = prevColor;
                            EditorGUILayout.LabelField($"at line [{entry.Line + 1}]");
                            if (GUILayout.Button("Copy", GUILayout.Width(50f)))
                            {
                                GUIUtility.systemCopyBuffer = entry.ScriptGuid;
                            }
                            GUILayout.FlexibleSpace();
                            EditorGUILayout.EndHorizontal();
                        }

                        GUIUtilities.HorizontalLine();
                    }

                    if (_enableDuplicateComponentScan && _outputSettings.ShowDuplicateComponents && asset.RefsData.DuplicateComponents.Count > 0)
                    {
                        prevColor = GUI.color;
                        GUI.color = Color.cyan;
                        EditorGUILayout.LabelField("Duplicate Components:");
                        GUI.color = prevColor;

                        foreach (var entry in asset.RefsData.DuplicateComponents)
                        {
                            EditorGUILayout.BeginHorizontal();
                            prevColor = GUI.color;
                            GUI.color = Color.cyan;
                            EditorGUILayout.LabelField($"  [{entry.GameObjectName}] {entry.ComponentType} x{entry.Count}", GUILayout.Width(400f));
                            GUI.color = prevColor;
                            GUILayout.FlexibleSpace();
                            EditorGUILayout.EndHorizontal();
                        }

                        GUIUtilities.HorizontalLine();
                    }

                    if (_enableInvalidLayerScan && _outputSettings.ShowInvalidLayers && asset.RefsData.InvalidLayers.Count > 0)
                    {
                        prevColor = GUI.color;
                        GUI.color = Color.yellow;
                        EditorGUILayout.LabelField("Invalid Layer References:");
                        GUI.color = prevColor;

                        foreach (var entry in asset.RefsData.InvalidLayers)
                        {
                            EditorGUILayout.BeginHorizontal();
                            prevColor = GUI.color;
                            GUI.color = Color.yellow;
                            EditorGUILayout.LabelField($"  Layer {entry.LayerIndex} (undefined)", GUILayout.Width(400f));
                            GUI.color = prevColor;
                            EditorGUILayout.LabelField($"at line [{entry.Line + 1}]");
                            GUILayout.FlexibleSpace();
                            EditorGUILayout.EndHorizontal();
                        }

                        GUIUtilities.HorizontalLine();
                    }
                }
                
                GUIUtilities.HorizontalLine();
            }

            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndVertical();
            GUILayout.EndScrollView();
        }
        
        private void DrawAnalysisControlSection()
        {
            EditorGUILayout.BeginHorizontal();
            
            GUILayout.FlexibleSpace();
            
            var prevColor = GUI.color;
            GUI.color = Color.green;

            if (_analysisOngoing)
            {
                GUILayout.Label("Analysis ongoing...");
            }
            else
            {
                if (GUILayout.Button("Run Analysis", GUILayout.Width(300f)))
                {
                    PocketEditorCoroutine.Start(PopulateMissingReferencesList(), this);
                }
            }

            GUI.color = prevColor;
            
            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndHorizontal();

            _settingsFoldout = EditorGUILayout.Foldout(_settingsFoldout, "Analysis Settings");

            if (_settingsFoldout)
            {
                EditorGUI.indentLevel++;
                
                EditorGUILayout.LabelField(new GUIContent("Additionaly Scan For:", "Missing References are scanned by default. However you can enable/disable additional analysis options below."));

                _enableMissingMethodScan = EditorGUILayout.Toggle(
                    new GUIContent("<Missing> Methods",
                        "Check UnityEvent references for methods that no longer exist on the target type.\n" +
                        "Detects '<Missing>' callbacks in buttons, events, etc.\n" +
                        "Scans m_TargetAssemblyTypeName + m_MethodName pairs via reflection."),
                    _enableMissingMethodScan);

                _enableTypeMismatchScan = EditorGUILayout.Toggle(
                    new GUIContent("Type Mismatches",
                        "Check UnityEvent argument types that cannot be resolved in any loaded assembly.\n" +
                        "Detects 'Type Mismatch' issues where m_ObjectArgumentAssemblyTypeName\n" +
                        "references a class that has been deleted or renamed."),
                    _enableTypeMismatchScan);

                _enableMissingScriptScan = EditorGUILayout.Toggle(
                    new GUIContent("Missing Scripts",
                        "Check MonoBehaviour components referencing deleted/missing scripts.\n" +
                        "Scans m_Script GUID references in YAML and verifies the script asset exists.\n" +
                        "Works for both prefabs and scenes."),
                    _enableMissingScriptScan);

                _enableDuplicateComponentScan = EditorGUILayout.Toggle(
                    new GUIContent("Duplicate Components",
                        "Check for duplicate component types on GameObjects in prefabs.\n" +
                        "Uses runtime API to inspect component hierarchies.\n" +
                        "Reports any component type appearing more than once on the same GameObject."),
                    _enableDuplicateComponentScan);

                _enableInvalidLayerScan = EditorGUILayout.Toggle(
                    new GUIContent("Invalid Layers",
                        "Check for GameObject layer values not defined in TagManager.\n" +
                        "Loads valid layers from ProjectSettings/TagManager.asset and scans\n" +
                        "m_Layer values in prefabs and scenes for undefined layer indices."),
                    _enableInvalidLayerScan);

                EditorGUI.indentLevel--;
            }
        }
        
        private void DrawInfoSection()
        {
            EditorGUILayout.LabelField("* [Missing FileID and Guid] - in 100% of cases indicates an error");
            EditorGUILayout.LabelField("* [Missing Guid] - most likely indicates an error");
            EditorGUILayout.LabelField("Sometimes missing GUID can be compensated by FileID so we are not 100% that there is a problem");
            EditorGUILayout.LabelField("However the tool still marks it as a warning for you to investigate");
            
            EditorGUILayout.LabelField("* Other FileID issues most likely do not indicate errors and are hidden by default");
            
            _infoFoldout = EditorGUILayout.Foldout(_infoFoldout, "Full Info");

            if (!_infoFoldout)
            {
                return;
            }
            
            EditorGUILayout.BeginVertical();
                
            EditorGUILayout.LabelField("Unity uses FileID and GUID entities to identify and assign assets to each other");
            EditorGUILayout.LabelField("This tool scans all assets to find all FileIDs and / or GUIDs that are assigned to a field");
            EditorGUILayout.LabelField("but do not exist neither in current asset nor in all project");
                
            GUIUtilities.HorizontalLine();
                
            EditorGUILayout.LabelField("There are several types of issues that might occur during this analysis");

            var prevColor = GUI.color;
            GUI.color = Color.red;
                
            EditorGUILayout.LabelField("[Missing FileID and Guid] - both identifiers do not exist");
            EditorGUILayout.LabelField("* in that case we are 100% sure that there is a missing reference so we mark it with red color");
                
            GUI.color = Color.yellow;
                
            EditorGUILayout.LabelField("[Missing Guid] - only Guid does not exist");
            EditorGUILayout.LabelField("[Missing FileId] - only FileId does not exist");
                
            EditorGUILayout.LabelField("* this issues need further investigation by you");
            EditorGUILayout.LabelField("* there might be or not a missing reference since it can be somehow processed by some internal Unity code");
            EditorGUILayout.LabelField("* however [Missing Guid] most of the times indicates that there are some errors so we mark it as yellow");
            EditorGUILayout.LabelField("* [Missing FileId] usually involves more internal nuances and less likely indicates an error and so we mark it as non-warning (cyan)");
                
            GUI.color = Color.white;
                
            GUIUtilities.HorizontalLine();
                
            GUI.color = Color.cyan;
                
            EditorGUILayout.LabelField("In most cases you just need to fix [Missing FileID and Guid] and [Missing Guid] issues");
            EditorGUILayout.LabelField("That is why other filters are hidden by default and most of the users won't need them");
                
            GUI.color = Color.white;
                
            GUIUtilities.HorizontalLine();
                
            EditorGUILayout.LabelField("* please also note that not all missing references are presented in Unity inspector");
            EditorGUILayout.LabelField("* some of them might be hidden if current serialization doesn't cover fields that contain errors");
            EditorGUILayout.LabelField("* or some of them might be replaced by custom inspectors etc etc");
                
            GUI.color = Color.yellow;
                
            EditorGUILayout.LabelField("* so in some cases you need to enable Debug inspector view or even dive into the asset file text contents");
                
            GUI.color = Color.white;

            GUIUtilities.HorizontalLine();
                
            EditorGUILayout.LabelField("This tool also collects some other info:");
                
            EditorGUILayout.LabelField("[Missing Local FileID] - might indicate that there is some issue with internal objects referencing each other");
            EditorGUILayout.LabelField("[Empty Local FileID] - might indicate an empty internal field");
            EditorGUILayout.LabelField("* these two fields provide some very specific info that is rarely needed for most of users");

            if (_enableMissingMethodScan)
            {
                GUIUtilities.HorizontalLine();

                GUI.color = Color.magenta;
                EditorGUILayout.LabelField("[<Missing> Methods] - UnityEvent references a method that no longer exists on the target type");
                EditorGUILayout.LabelField("* appears when a script method referenced by a UnityEvent (button click, etc.) has been renamed or deleted");
                EditorGUILayout.LabelField("* shows as \"<Missing>\" in the Unity Inspector dropdown");
            }

            if (_enableTypeMismatchScan)
            {
                if (!_enableMissingMethodScan)
                    GUIUtilities.HorizontalLine();

                GUI.color = new Color(1f, 0.5f, 0f);
                EditorGUILayout.LabelField("[Type Mismatch] - UnityEvent argument references a type that cannot be resolved");
                EditorGUILayout.LabelField("* appears when the object argument type in a UnityEvent has been deleted or renamed");
                EditorGUILayout.LabelField("* the type name is stored in m_ObjectArgumentAssemblyTypeName but the class no longer exists");
            }

            if (_enableMissingScriptScan)
            {
                GUIUtilities.HorizontalLine();

                GUI.color = Color.red;
                EditorGUILayout.LabelField("[Missing Scripts] - MonoBehaviour components referencing deleted or missing scripts");
                EditorGUILayout.LabelField("* appears when a script file referenced by a component has been deleted or moved");
                EditorGUILayout.LabelField("* shows as \"Missing (Mono Script)\" in the Unity Inspector");
            }

            if (_enableDuplicateComponentScan)
            {
                GUIUtilities.HorizontalLine();

                GUI.color = Color.cyan;
                EditorGUILayout.LabelField("[Duplicate Components] - Multiple components of the same type on a single GameObject");
                EditorGUILayout.LabelField("* reports any component type appearing more than once on the same GameObject in prefabs");
                EditorGUILayout.LabelField("* some duplicates may be intentional (e.g. multiple colliders); review before fixing");
            }

            if (_enableInvalidLayerScan)
            {
                GUIUtilities.HorizontalLine();

                GUI.color = Color.yellow;
                EditorGUILayout.LabelField("[Invalid Layers] - GameObject layer values not defined in TagManager");
                EditorGUILayout.LabelField("* appears when a GameObject's m_Layer references an index with no name in TagManager");
                EditorGUILayout.LabelField("* valid layers are loaded from ProjectSettings/TagManager.asset");
            }

            GUI.color = Color.white;

            EditorGUILayout.EndVertical();
                
            GUI.color = prevColor;
                
            GUIUtilities.HorizontalLine();
        }
    }

    public class LocalReferenceRegistry
    {
        public LocalReferenceRegistry(long id, int line)
        {
            Id = id;
            IdStr = id.ToString();
            Line = line;
        }

        public bool IdValid => Id > 0;

        public long Id { get; }
        public string IdStr { get; }
        public int Line { get; }
        
        public int LocalUsagesCount { get; set; }
        public bool ExistsInAssets { get; set; }
    }

    public class EmptyLocalFileIDRegistry
    {
        public EmptyLocalFileIDRegistry(int line)
        {
            Line = line;
        }
        
        public int Line { get; }
    }

    public class MissingMethodEntry
    {
        public MissingMethodEntry(string className, string methodName, int line)
        {
            ClassName = className;
            MethodName = methodName;
            Line = line;
        }

        public string ClassName { get; }
        public string MethodName { get; }
        public int Line { get; }
    }

    public class TypeMismatchEntry
    {
        public TypeMismatchEntry(string typeName, int line)
        {
            TypeName = typeName;
            Line = line;
        }

        public string TypeName { get; }
        public int Line { get; }
    }

    public class MissingScriptEntry
    {
        public MissingScriptEntry(string scriptGuid, int line)
        {
            ScriptGuid = scriptGuid;
            Line = line;
        }

        public string ScriptGuid { get; }
        public int Line { get; }
    }

    public class DuplicateComponentEntry
    {
        public DuplicateComponentEntry(string componentType, int count, string gameObjectName)
        {
            ComponentType = componentType;
            Count = count;
            GameObjectName = gameObjectName;
        }

        public string ComponentType { get; }
        public int Count { get; }
        public string GameObjectName { get; }
    }

    public class InvalidLayerEntry
    {
        public InvalidLayerEntry(int layerIndex, int line)
        {
            LayerIndex = layerIndex;
            Line = line;
        }

        public int LayerIndex { get; }
        public int Line { get; }
    }

    public class ExternalReferenceRegistry
    {
        public ExternalReferenceRegistry(
            bool fileIdValid, bool guidValid,
            long fileId, string guid, int line)
        {
            FileIDValid = fileIdValid;
            GuidValid = guidValid;
            FileID = fileId;
            Guid = guid;
            Line = line;
        }

        public bool FileIDValid { get; }
        public bool GuidValid { get; }
        
        public long FileID { get; }
        public string Guid { get; }
        
        public int Line { get; }

        public string FieldType { get; set; }

        public List<string> Sample { get; } = new List<string>();
        public bool FileIDExistsInAssets { get; set; }
        public bool GuidExistsInAssets { get; set; }
        public string HolderName { get; set; }
        
        public int WarningLevel { get; private set; }

        public void UpdateWarningLevel()
        {
            WarningLevel = 0;

            if (FileIDValid && !FileIDExistsInAssets)
                WarningLevel++;

            if (GuidValid && !GuidExistsInAssets)
                WarningLevel++;
        }
    }
    
    public class AssetReferencesData
    {
        public List<ExternalReferenceRegistry> ExternalReferences { get; } = new List<ExternalReferenceRegistry>();
        public List<LocalReferenceRegistry> LocalReferences { get; } = new List<LocalReferenceRegistry>();
        public List<EmptyLocalFileIDRegistry> EmptyFileIDs { get; } = new List<EmptyLocalFileIDRegistry>();
        public List<MissingMethodEntry> MissingMethods { get; } = new List<MissingMethodEntry>();
        public List<TypeMismatchEntry> TypeMismatches { get; } = new List<TypeMismatchEntry>();
        public List<MissingScriptEntry> MissingScripts { get; } = new List<MissingScriptEntry>();
        public List<DuplicateComponentEntry> DuplicateComponents { get; } = new List<DuplicateComponentEntry>();
        public List<InvalidLayerEntry> InvalidLayers { get; } = new List<InvalidLayerEntry>();
       
        public int MissingFileIDAndGuid { get; private set; }
        public int MissingGuid { get; private set; }
        public int MissingFileID { get; private set; }
        public int MissingLocalFileID { get; private set; }

        public bool HasWarnings => MissingFileIDAndGuid > 0 || MissingGuid > 0
            || MissingMethods.Count > 0 || TypeMismatches.Count > 0
            || MissingScripts.Count > 0 || DuplicateComponents.Count > 0 || InvalidLayers.Count > 0;

        public void CalculateCounters()
        {
            MissingFileIDAndGuid = ExternalReferences.Count(x => x.FileIDValid && x.GuidValid && !x.FileIDExistsInAssets && !x.GuidExistsInAssets);
            MissingGuid = ExternalReferences.Count(x => x.GuidValid && !x.GuidExistsInAssets);
            MissingFileID = ExternalReferences.Count(x => x.FileIDValid && !x.FileIDExistsInAssets && LocalReferences.All(l => l.Id != x.FileID));
            MissingLocalFileID = LocalReferences.Count(x => x.IdValid && x.LocalUsagesCount == 0 && !x.ExistsInAssets);

            foreach (var registry in ExternalReferences)
            {
                registry.UpdateWarningLevel();
            }
        }
    }
    
    public class AssetData
    {
        public AssetData(string path, Type type, string typeName, string guid, AssetReferencesData refsData)
        {
            Path = path;
            Type = type;
            TypeName = typeName;
            Guid = guid;
            RefsData = refsData;
        }

        public string Path { get; }
        public Type Type { get; }
        public string TypeName { get; }
        public string Guid { get; }
        public AssetReferencesData RefsData { get; }
        public HashSet<string> MissingFieldTypes { get; } = new HashSet<string>();
        public bool ValidType => Type != null;
        
        public bool Foldout { get; set; }
    }

    public static class GUIUtilities
    {
        private static void HorizontalLine(
            int marginTop,
            int marginBottom,
            int height,
            Color color
        )
        {
            EditorGUILayout.BeginHorizontal();
            var rect = EditorGUILayout.GetControlRect(
                false,
                height,
                new GUIStyle { margin = new RectOffset(0, 0, marginTop, marginBottom) }
            );

            EditorGUI.DrawRect(rect, color);
            EditorGUILayout.EndHorizontal();
        }

        public static void HorizontalLine(
            int marginTop = 5,
            int marginBottom = 5,
            int height = 2
        )
        {
            HorizontalLine(marginTop, marginBottom, height, new Color(0.5f, 0.5f, 0.5f, 1));
        }
    }
    
    internal class PocketEditorCoroutine
    {
        private readonly bool _hasOwner;
        private readonly WeakReference _ownerReference;
        private IEnumerator _routine;
        private double? _lastTimeWaitStarted;

        public static PocketEditorCoroutine Start(IEnumerator routine, EditorWindow owner = null)
        {
            return new PocketEditorCoroutine(routine, owner);
        }
        
        private PocketEditorCoroutine(IEnumerator routine, EditorWindow owner = null)
        {
            _routine = routine ?? throw new ArgumentNullException(nameof(routine));
            EditorApplication.update += OnUpdate;
            if (owner == null) return;
            _ownerReference = new WeakReference(owner);
            _hasOwner = true;
        }

        public void Stop()
        {
            EditorApplication.update -= OnUpdate;
            _routine = null;
        }
        
        private void OnUpdate()
        {
            if (_hasOwner && (_ownerReference == null || _ownerReference is { IsAlive: false }))
            {
                Stop();
                return;
            }
            
            var result = MoveNext(_routine);
            if (!result.HasValue || result.Value) return;
            Stop();
        }

        private bool? MoveNext(IEnumerator enumerator)
        {
            if (enumerator.Current is not float current) 
                return enumerator.MoveNext();
            
            _lastTimeWaitStarted ??= EditorApplication.timeSinceStartup;
            
            if (!(_lastTimeWaitStarted.Value + current
                  <= EditorApplication.timeSinceStartup))
                return null;

            _lastTimeWaitStarted = null;
            return enumerator.MoveNext();
        }
    }
}