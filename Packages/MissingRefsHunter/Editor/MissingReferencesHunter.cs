// #define LOGS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

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
            public string OutputDescription { get; set; }
            
            public int UnknownExternalGuidsCases { get; set; }
            public int UnknownLocalFileIDsCases { get; set; }
            public int CasesWithNoWarnings { get; set; }
        }

        private class OutputSettings
        {
            public const int PageSize = 50;

            public int? PageToShow { get; set; }
            
            public string PathFilter { get; set; }
            
            public bool ShowUnknownGuids { get; set; } = true;
            public bool ShowLocalRefsWarnings { get; set; } = true;
            public bool ShowAssetsWithNoWarnings { get; set; }
        }
        
        private Result _result;
        private OutputSettings _outputSettings;

        private bool _infoFoldout;
        
        private Vector2 _pagesScroll = Vector2.zero;

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

        [MenuItem("Tools/Missing References Hunter")]
        public static void LaunchUnreferencedAssetsWindow()
        {
            GetWindow<MissingReferencesWindow>();
        }
        
        [MenuItem("Assets/Find Missing References", false, 20)]
        public static void FindReferences()
        {
            var window = GetWindow<MissingReferencesWindow>();
            window.PopulateMissingReferencesForFiles();
        }

        public void PopulateMissingReferencesForFiles()
        {
            Show();

            var filesToAnalyze = new List<string>();
            var selected = Selection.objects;

            foreach (var selectedObject in selected)
            {
                var selectedObjectPath = AssetDatabase.GetAssetPath(selectedObject);
                if (!string.IsNullOrWhiteSpace(selectedObjectPath))
                {
                    filesToAnalyze.Add(selectedObjectPath);
                    Debug.Log("Selected: " + selectedObjectPath);
                }
            }

            PopulateMissingReferencesList(filesToAnalyze);
        }

        /// <summary>
        /// Finds missing references.
        /// </summary>
        /// <param name="filesToAnalyze">If empty then script performs all files analysis</param>
        private void PopulateMissingReferencesList(List<string> filesToAnalyze)
        {
            _result = new Result();
            _outputSettings = new OutputSettings
            {
                PageToShow = 0
            };

            Clear();
            Show();

            var assetPaths = AssetDatabase.GetAllAssetPaths().ToList();

            EditorUtility.ClearProgressBar();
            
            var regexFileAndGuid = new Regex(@"fileID: \d+, guid: [a-f0-9]" + "{" + "32" + "}");
            var regexFileId = new Regex(@"{fileID: \d+}");
            
            var count = 0;
            foreach (var assetPath in assetPaths)
            {
                EditorUtility.DisplayProgressBar("Missing References", "Searching for missing references",
                    (float) count / assetPaths.Count);
                count++;

                var guidStr = AssetDatabase.AssetPathToGUID(assetPath);

                _result.Guids.Add(guidStr);

                if (filesToAnalyze.Count > 0 && !filesToAnalyze.Contains(assetPath))
                    continue;

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

                                var externalGuid = str.Substring(str.Length - 32);

                                if (!externalGuid.StartsWith("0000000000"))
                                {
#if LOGS
                                    Debug.Log($"Found Guid: {externalGuid} [Length: {externalGuid.Length}] at {index}");
                                    Debug.Log( AssetDatabase.GUIDToAssetPath(externalGuid));
#endif
                                    refsData.ExternalGuids.Add(new ExternalGuidRegistry(externalGuid, index));
                                }
#if LOGS
                                else
                                {
                                    Debug.Log("Guid: " + externalGuid + " ignored");
                                }
#endif
                            }
                        }

                        if (line.Contains("fileID:"))
                        {
                            var fileIdMatches = regexFileId.Matches(line);

                            for (var i = 0; i < fileIdMatches.Count; i++)
                            {
                                var match = fileIdMatches[i];
                                var str = match.Value;

                                str = str.Substring(9);
                                str = str.Replace("}", string.Empty);

                                var localFileId = str;

#if LOGS
                                Debug.Log($"Found FileID: {localFileId} [Length: {localFileId.Length}] at {index}");
#endif

                                if (localFileId == "0")
                                    refsData.EmptyFileIds.Add(new EmptyLocalFileIdRegistry(index));
                                else
                                    refsData.LocalIds.Add(new LocalFileIdRegistry(localFileId, index));
                            }
                        }
                    }
                }

                foreach (var localId in refsData.LocalIds)
                {
                    for (var index = 0; index < lines.Length; index++)
                    {
                        var line = lines[index];
                        if (index != localId.Line && line.Contains(localId.Id))
                        {
                            localId.UsagesCount++;
                        }
                    }
                }
                
                _result.Assets.Add(new AssetData(assetPath, type, typeName, guidStr, refsData));
            }
            
            foreach (var asset in _result.Assets)
            {
                foreach (var guid in asset.RefsData.ExternalGuids)
                {
                    if (_result.Guids.Contains(guid.Id))
                    {
                        guid.Used = true;
                    }
                }
            }

            foreach (var asset in _result.Assets)
            {
                asset.RefsData.CalculateCounters();
            }

            _result.UnknownExternalGuidsCases = _result.Assets.Count(x => x.RefsData.UnknownExternalRefs > 0);
            _result.UnknownLocalFileIDsCases = _result.Assets.Count(x => x.RefsData.UnknownLocalRefs > 0 
                                                                         || x.RefsData.EmptyFileIds.Count > 0);
            _result.CasesWithNoWarnings = _result.Assets.Count(x => !x.RefsData.HasWarnings);
            
            var assetsWithWarnings = _result.Assets.Count - _result.CasesWithNoWarnings;
            
            _result.OutputDescription = $"Assets with Warnings: {assetsWithWarnings}";

            EditorUtility.ClearProgressBar();
            
            Debug.Log(_result.OutputDescription);

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
                    typeName = "Unknown Type";
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
            
            bool DerivesFromOrEqual(Type a, Type b)
            {
#if UNITY_WSA && ENABLE_DOTNET && !UNITY_EDITOR
                return b == a || b.GetTypeInfo().IsAssignableFrom(a.GetTypeInfo());
#else
                return b == a || b.IsAssignableFrom(a);
#endif
            }
        }

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
        
        private static void Clear()
        {
            EditorUtility.UnloadUnusedAssetsImmediate();
        }

        private void OnGUI()
        {
            GUIUtilities.HorizontalLine();

            EditorGUILayout.BeginHorizontal();
            
            GUILayout.FlexibleSpace();
            
            var prevColor = GUI.color;
            GUI.color = Color.green;
            
            if (GUILayout.Button("Run Analysis", GUILayout.Width(300f)))
            {
                PopulateMissingReferencesList(new List<string>());
            }
            
            GUI.color = prevColor;
            
            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndHorizontal();
            
            GUIUtilities.HorizontalLine();
            
            _infoFoldout = EditorGUILayout.Foldout(_infoFoldout, "Info");

            if (_infoFoldout)
            {
                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField("[Unknown Guids] - assets that has references to assets that no longer exists");
                EditorGUILayout.LabelField("[Local Refs Warnings] - assets that has:");
                EditorGUILayout.LabelField("* internal refs to child objects that no longer exists");
                EditorGUILayout.LabelField("* internal refs with null values e.g. empty array item");
                EditorGUILayout.LabelField("[Assets With No Warnings] - assets with valid references only");
                EditorGUILayout.EndVertical();
            }
            
            GUIUtilities.HorizontalLine();

            if (_result == null)
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
                if (asset.RefsData.UnknownExternalRefs > 0 && _outputSettings.ShowUnknownGuids)
                {
                    filteredAssets.Add(asset);
                }

                if ((asset.RefsData.UnknownLocalRefs > 0 || asset.RefsData.EmptyFileIds.Count > 0) &&
                    _outputSettings.ShowLocalRefsWarnings)
                {
                    filteredAssets.Add(asset);
                }

                if (!asset.RefsData.HasWarnings && _outputSettings.ShowAssetsWithNoWarnings)
                {
                    filteredAssets.Add(asset);
                }
            }
    
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(_result.OutputDescription);

            EditorGUILayout.EndHorizontal();

            _pagesScroll = EditorGUILayout.BeginScrollView(_pagesScroll);

            EditorGUILayout.BeginHorizontal();
            
            prevColor = GUI.color;
            GUI.color = !_outputSettings.PageToShow.HasValue ? Color.yellow : Color.white;

            if (GUILayout.Button("All", GUILayout.Width(30f)))
            {
                _outputSettings.PageToShow = null;
            }

            GUI.color = prevColor;
            
            var totalCount = filteredAssets.Count;
            var pagesCount = totalCount / OutputSettings.PageSize + (totalCount % OutputSettings.PageSize > 0 ? 1 : 0);

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
            
            prevColor = GUI.color;
            
            var assetsWithUnknownGuids = $"Unknown Guids [{_result.UnknownExternalGuidsCases}]: ";
            GUI.color = _outputSettings.ShowUnknownGuids ? Color.cyan : Color.gray;
            if (GUILayout.Button(assetsWithUnknownGuids + (_outputSettings.ShowUnknownGuids ? "Shown" : "Hidden")))
            {
                _outputSettings.ShowUnknownGuids = !_outputSettings.ShowUnknownGuids;
            }

            var assetsWithLocalRefsWarnings = $"Local Refs Warnings [{_result.UnknownLocalFileIDsCases}]: ";
            GUI.color = _outputSettings.ShowLocalRefsWarnings ? Color.cyan : Color.gray;
            if (GUILayout.Button(assetsWithLocalRefsWarnings + (_outputSettings.ShowLocalRefsWarnings ? "Shown" : "Hidden")))
            {
                _outputSettings.ShowLocalRefsWarnings = !_outputSettings.ShowLocalRefsWarnings;
            }
            
            var assetsWithNoWarningLabel = $"Assets With No Warnings [{_result.CasesWithNoWarnings}]: ";
            GUI.color = _outputSettings.ShowAssetsWithNoWarnings ? Color.cyan : Color.gray;
            if (GUILayout.Button(assetsWithNoWarningLabel + (_outputSettings.ShowAssetsWithNoWarnings ? "Shown" : "Hidden")))
            {
                _outputSettings.ShowAssetsWithNoWarnings = !_outputSettings.ShowAssetsWithNoWarnings;
            }

            GUI.color = prevColor;
            
            textFieldStyle.alignment = prevTextFieldAlignment;
            
            EditorGUILayout.EndHorizontal();
            
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
                
                EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(25f));
                
                prevColor = GUI.color;
                GUI.color = asset.ValidType ? Color.white : Color.red;
                EditorGUILayout.LabelField(asset.TypeName, GUILayout.Width(100f));    
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
                GUI.color = asset.RefsData.UnknownExternalRefs > 0 ? Color.red : Color.green;
                EditorGUILayout.LabelField("Unknown Guids: " + asset.RefsData.UnknownExternalRefs, GUILayout.Width(120f));
                GUI.color = asset.RefsData.UnknownLocalRefs > 0 ? Color.yellow : Color.green;
                EditorGUILayout.LabelField("Unknown FileIDs: " + asset.RefsData.UnknownLocalRefs, GUILayout.Width(120f));
                GUI.color = asset.RefsData.EmptyFileIds.Count > 0 ? Color.yellow : Color.green;
                EditorGUILayout.LabelField("Empty FileIDs: " + asset.RefsData.EmptyFileIds.Count, GUILayout.Width(120f));    
                GUI.color = prevColor;
                
                prevColor = GUI.color;
                GUI.color = Color.gray;
                EditorGUILayout.LabelField(asset.Guid, GUILayout.Width(250f));
                GUI.color = prevColor;
                
                EditorGUILayout.LabelField(asset.Path);
                
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndVertical();
            GUILayout.EndScrollView();
        }
        
        private void OnDestroy()
        {
            Clear();
        }
    }

    public class LocalFileIdRegistry
    {
        public LocalFileIdRegistry(string id, int line)
        {
            Id = id;
            Line = line;
        }

        public string Id { get; }
        public int Line { get; }
        
        public int UsagesCount { get; set; }
    }

    public class EmptyLocalFileIdRegistry
    {
        public EmptyLocalFileIdRegistry(int line)
        {
            Line = line;
        }
        
        public int Line { get; }
    }

    public class ExternalGuidRegistry
    {
        public ExternalGuidRegistry(string id, int line)
        {
            Id = id;
            Line = line;
        }

        public string Id { get; }
        public int Line { get; }

        public bool Used { get; set; }
    }
    
    public class AssetReferencesData
    {
        public List<EmptyLocalFileIdRegistry> EmptyFileIds { get; } = new List<EmptyLocalFileIdRegistry>();
        public List<ExternalGuidRegistry> ExternalGuids { get; } = new List<ExternalGuidRegistry>();
        public List<LocalFileIdRegistry> LocalIds { get; } = new List<LocalFileIdRegistry>();
        
        public int UnknownExternalRefs { get; private set; }
        public int UnknownLocalRefs { get; private set; }

        public bool HasWarnings => EmptyFileIds.Count > 0 || UnknownExternalRefs > 0 || UnknownLocalRefs > 0;

        public void CalculateCounters()
        {
            UnknownExternalRefs = ExternalGuids.Count(x => !x.Used);
            UnknownLocalRefs = LocalIds.Count(x => x.UsagesCount == 0);
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
        public bool ValidType => Type != null;
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
}