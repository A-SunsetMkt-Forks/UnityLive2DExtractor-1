using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using AssetStudio;
using Object = AssetStudio.Object;

namespace UnityLive2DExtractor
{
    static class Program
    {
        private static readonly Dictionary<MonoBehaviour, CubismModel> L2dModelDict = new Dictionary<MonoBehaviour, CubismModel>();
        private static readonly Dictionary<Object, string> Containers = new Dictionary<Object, string>();

        static void Main(string[] args)
        {
            var appAssembly = typeof(Program).Assembly.GetName();
            var arch = Environment.Is64BitProcess ? "x64" : "x32";
            var frameworkName = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName;
            Console.Title = $"{appAssembly.Name} v{appAssembly.Version} [{arch}] [{frameworkName}]";

            if (args.Length != 1)
            {
                Console.WriteLine("Usage: \nUnityLive2DExtractor <path to a Live2D folder>");
                ExitMsg();
                return;
            }
            if (!Directory.Exists(args[0]))
            {
                Console.WriteLine($"{"[Error]".Color(ColorConsole.BrightRed)} Invalid input path \"{args[0]}\"\nSpecified folder was not found");
                ExitMsg();
                return;
            }
            Progress.Default = new Progress<int>(ShowCurProgressValue);

            Console.WriteLine("Loading...");
            var baseDestPath = Path.Combine(Path.GetDirectoryName(args[0]), "Live2DOutput");
            var modelGroupOption = Live2DModelGroupOption.ContainerPath;
            var motionMode = Live2DMotionMode.MonoBehaviour;
            var searchByFilename = false;
            var forceBezier = false;
            var assetsManager = new AssetsManager();
            assetsManager.SetAssetFilter(
                ClassIDType.Animation,
                ClassIDType.AnimationClip,
                ClassIDType.AnimatorController,
                ClassIDType.MonoBehaviour,
                ClassIDType.Texture2D
            );
            assetsManager.LoadFilesAndFolders(args[0]);
            if (assetsManager.AssetsFileList.Count == 0)
            {
                Console.WriteLine("No Unity file can be loaded");
                ExitMsg();
                return;
            }
            if (args[0].EndsWith($"{Path.DirectorySeparatorChar}"))
                args[0] = args[0].Remove(args[0].Length - 1);

            foreach (var assetsFile in assetsManager.AssetsFileList)
            {
                var preloadTable = new List<PPtr<Object>>();
                foreach (var asset in assetsFile.Objects)
                {
                    switch (asset)
                    {
                        case PreloadData m_PreloadData:
                            preloadTable = m_PreloadData.m_Assets;
                            break;
                        case AssetBundle m_AssetBundle:
                            var isStreamedSceneAssetBundle = m_AssetBundle.m_IsStreamedSceneAssetBundle;
                            if (!isStreamedSceneAssetBundle)
                            {
                                preloadTable = m_AssetBundle.m_PreloadTable;
                            }
                            foreach (var m_Container in m_AssetBundle.m_Container)
                            {
                                var preloadIndex = m_Container.Value.preloadIndex;
                                var preloadSize = isStreamedSceneAssetBundle
                                    ? preloadTable.Count
                                    : m_Container.Value.preloadSize;
                                var preloadEnd = preloadIndex + preloadSize;
                                for (var k = preloadIndex; k < preloadEnd; k++)
                                {
                                    var pptr = preloadTable[k];
                                    if (pptr.TryGet(out var obj))
                                    {
                                        Containers[obj] = m_Container.Key;
                                    }
                                }
                            }
                            break;
                        case ResourceManager m_ResourceManager:
                            foreach (var m_Container in m_ResourceManager.m_Container)
                            {
                                if (m_Container.Value.TryGet(out var obj))
                                {
                                    Containers[obj] = m_Container.Key;
                                }
                            }
                            break;
                        case MonoBehaviour m_MonoBehaviour when m_MonoBehaviour.m_Script.TryGet(out var m_Script):
                            switch (m_Script.m_ClassName)
                            {
                                case "CubismMoc":
                                    if (!L2dModelDict.ContainsKey(m_MonoBehaviour))
                                    {
                                        L2dModelDict.Add(m_MonoBehaviour, null);
                                    }
                                    break;
                                case "CubismRenderer":
                                    BindCubismAsset(m_MonoBehaviour, CubismParsers.CubismMonoBehaviourType.RenderTexture);
                                    break;
                                case "CubismDisplayInfoParameterName":
                                    BindCubismAsset(m_MonoBehaviour, CubismParsers.CubismMonoBehaviourType.DisplayInfo, isParamInfo: true);
                                    break;
                                case "CubismDisplayInfoPartName":
                                    BindCubismAsset(m_MonoBehaviour, CubismParsers.CubismMonoBehaviourType.DisplayInfo);
                                    break;
                                case "CubismPosePart":
                                    BindCubismAsset(m_MonoBehaviour, CubismParsers.CubismMonoBehaviourType.PosePart);
                                    break;
                            }
                            break;
                        case GameObject m_GameObject when m_GameObject.CubismModel != null:
                            if (TryGetCubismMoc(m_GameObject.CubismModel.CubismModelMono, out var mocMono))
                            {
                                L2dModelDict[mocMono] = m_GameObject.CubismModel;
                                if (Containers.TryGetValue(m_GameObject, out var container))
                                {
                                    m_GameObject.CubismModel.Container = container;
                                }
                                BindAnimationClips(m_GameObject);
                            }
                            break;
                    }
                }
            }

            if (L2dModelDict.Count == 0)
            {
                Console.WriteLine("Live2D Cubism models were not found");
                ExitMsg();
                return;
            }

            Console.WriteLine("Searching for Live2D files...");
            var mocPathDict = GenerateMocPathDict(L2dModelDict, searchByFilename);
            if (!searchByFilename && mocPathDict.Count != L2dModelDict.Count)
            {
                Console.WriteLine($"{"[Warning]".Color(ColorConsole.BrightYellow)} Some Live2D models cannot be exported with this extractor\n" +
                                  $"{"[Warning]".Color(ColorConsole.BrightYellow)} Try using AssetStudioModCLI with \"--l2d-search-by-filename\" flag instead\n");
            }
            if (L2dModelDict.Keys.First().serializedType?.m_Type == null)
            {
                Console.WriteLine($"{"[Warning]".Color(ColorConsole.BrightYellow)} Loaded files may require specifying an assembly folder for proper extraction,\n" +
                                  $"{"[Warning]".Color(ColorConsole.BrightYellow)} which is not supported in this extractor\n" +
                                  $"{"[Warning]".Color(ColorConsole.BrightYellow)} Please use AssetStudioModCLI with the \"--assembly-folder <path>\" option for this\n");
            }

            //search related assets for each l2d model
            var assetGroupDict = new Dictionary<MonoBehaviour, List<Object>>();
            foreach (var mocPathKvp in mocPathDict)
            {
                var mocPath = searchByFilename
                    ? mocPathKvp.Key.assetsFile.fullName
                    : mocPathKvp.Value;
                var result = Containers.Select(assetKvp =>
                {
                    if (!assetKvp.Value.Contains(mocPath))
                        return null;
                    var mocPathSpan = mocPath.AsSpan();
                    var modelNameFromPath = mocPathSpan.Slice(mocPathSpan.LastIndexOf('/') + 1);
#if NET9_0_OR_GREATER
                    foreach (var range in assetKvp.Value.AsSpan().Split('/'))
                    {
                        if (modelNameFromPath.SequenceEqual(assetKvp.Value.AsSpan()[range]))
                            return assetKvp.Key;
                    }
#else
                    foreach (var str in assetKvp.Value.Split('/'))
                    {
                        if (modelNameFromPath.SequenceEqual(str.AsSpan()))
                            return assetKvp.Key;
                    }
#endif
                    return null;
                }).Where(x => x != null).ToList();

                if (result.Count > 0)
                {
                    assetGroupDict[mocPathKvp.Key] = result;
                }
            }

            var totalModelCount = assetGroupDict.Count;
            Console.WriteLine($"Found {totalModelCount} model(s)");
            var modelCounter = 0;
            foreach (var assetGroupKvp in assetGroupDict)
            {
                var srcContainer = Containers.TryGetValue(assetGroupKvp.Key, out var result)
                    ? result
                    : assetGroupKvp.Key.assetsFile.fullName;

                Console.WriteLine($"\n[{modelCounter + 1}/{totalModelCount}] Extracting from: \"{srcContainer.Color(ColorConsole.BrightCyan)}\"...");
                try
                {
                    var modelExtractor = new ModelExtractor(assetGroupKvp, L2dModelDict);
                    var filename = string.IsNullOrEmpty(modelExtractor.MocMono.assetsFile.originalPath)
                        ? Path.GetFileNameWithoutExtension(modelExtractor.MocMono.assetsFile.fileName)
                        : Path.GetFileNameWithoutExtension(modelExtractor.MocMono.assetsFile.originalPath);
                    var modelName = !string.IsNullOrEmpty(modelExtractor.Model?.Name)
                        ? modelExtractor.Model.Name
                        : filename;
                    Console.WriteLine($"Model name: \"{modelName}\"");

                    string modelPath;
                    switch (modelGroupOption)
                    {
                        case Live2DModelGroupOption.SourceFileName:
                            modelPath = filename;
                            break;
                        case Live2DModelGroupOption.ModelName:
                            modelPath = modelName;
                            break;
                        default: //ContainerPath
                            var container = searchByFilename && modelExtractor.Model != null
                                ? modelExtractor.Model.Container
                                : srcContainer;
                            container = container == assetGroupKvp.Key.assetsFile.fullName
                                ? filename
                                : container;
                            modelPath = Path.HasExtension(container)
                                ? container.Replace(Path.GetExtension(container), "")
                                : container;
                            break;
                    }

                    var destPath = Path.Combine(baseDestPath, modelPath);
                    modelExtractor.ExtractCubismModel(destPath, motionMode, forceBezier);
                    modelCounter++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{"[Error]".Color(ColorConsole.BrightRed)} Live2D model extract error: \"{srcContainer}\"\n{ex}");
                }
            }
            var status = modelCounter > 0
                ? $"\nFinished extracting [{modelCounter}/{totalModelCount}] Live2D model(s) to \"{Path.GetFullPath(baseDestPath).Color(ColorConsole.BrightCyan)}\""
                : "\nNothing extracted";
            Console.WriteLine(status);
            ExitMsg();
        }

        private static bool TryGetCubismMoc(MonoBehaviour m_MonoBehaviour, out MonoBehaviour mocMono)
        {
            mocMono = null;
            var pptrDict = (OrderedDictionary)CubismParsers.ParseMonoBehaviour(m_MonoBehaviour, CubismParsers.CubismMonoBehaviourType.Model)?["_moc"];
            if (pptrDict == null)
                return false;

            var mocPPtr = new PPtr<MonoBehaviour>
            {
                m_FileID = (int)pptrDict["m_FileID"],
                m_PathID = (long)pptrDict["m_PathID"],
                AssetsFile = m_MonoBehaviour.assetsFile
            };
            return mocPPtr.TryGet(out mocMono);
        }

        private static bool TryGetModelGameObject(Transform m_Transform, out GameObject m_GameObject)
        {
            m_GameObject = null;
            if (m_Transform == null)
                return false;

            while (m_Transform.m_Father.TryGet(out var m_Father))
            {
                m_Transform = m_Father;
                if (m_Transform.m_GameObject.TryGet(out m_GameObject) && m_GameObject.CubismModel != null)
                {
                    return true;
                }
            }
            return false;
        }

        private static void BindCubismAsset(MonoBehaviour m_MonoBehaviour, CubismParsers.CubismMonoBehaviourType type, bool isParamInfo = false)
        {
            if (!m_MonoBehaviour.m_GameObject.TryGet(out var m_GameObject))
                return;

            if (!TryGetModelGameObject(m_GameObject.m_Transform, out var modelGameObject))
                return;

            switch (type)
            {
                case CubismParsers.CubismMonoBehaviourType.PosePart:
                    modelGameObject.CubismModel.PosePartList.Add(m_MonoBehaviour);
                    break;
                case CubismParsers.CubismMonoBehaviourType.DisplayInfo when isParamInfo:
                    modelGameObject.CubismModel.ParamDisplayInfoList.Add(m_MonoBehaviour);
                    break;
                case CubismParsers.CubismMonoBehaviourType.DisplayInfo:
                    modelGameObject.CubismModel.PartDisplayInfoList.Add(m_MonoBehaviour);
                    break;
                case CubismParsers.CubismMonoBehaviourType.RenderTexture:
                    modelGameObject.CubismModel.RenderTextureList.Add(m_MonoBehaviour);
                    break;
            }
        }

        private static void BindAnimationClips(GameObject gameObject)
        {
            if (gameObject.m_Animator == null || gameObject.m_Animator.m_Controller.IsNull)
                return;

            if (!gameObject.m_Animator.m_Controller.TryGet(out var controller))
                return;

            AnimatorController animatorController;
            if (controller is AnimatorOverrideController overrideController)
            {
                if (!overrideController.m_Controller.TryGet(out animatorController))
                    return;
            }
            else
            {
                animatorController = (AnimatorController)controller;
            }

            foreach (var clipPptr in animatorController.m_AnimationClips)
            {
                if (clipPptr.TryGet(out var m_AnimationClip))
                {
                    gameObject.CubismModel.ClipMotionList.Add(m_AnimationClip);
                }
            }
        }

        private static Dictionary<MonoBehaviour, string> GenerateMocPathDict(Dictionary<MonoBehaviour, CubismModel> mocDict, bool searchByFilename)
        {
            var tempMocPathDict = new Dictionary<MonoBehaviour, (string, string)>();
            var mocPathDict = new Dictionary<MonoBehaviour, string>();
            foreach (var mocMono in L2dModelDict.Keys)
            {
                if (Containers.TryGetValue(mocMono, out var fullContainerPath))
                {
                    var pathSepIndex = fullContainerPath.LastIndexOf('/');
                    var basePath = pathSepIndex > 0
                        ? fullContainerPath.Substring(0, pathSepIndex)
                        : fullContainerPath;
                    tempMocPathDict.Add(mocMono, (fullContainerPath, basePath));
                }
                else if (searchByFilename)
                {
                    tempMocPathDict.Add(mocMono, (mocMono.assetsFile.fullName, mocMono.assetsFile.fullName));
                }
            }
            if (tempMocPathDict.Count > 0)
            {
                var basePathSet = tempMocPathDict.Values.Select(x => x.Item2).ToHashSet();
                var useFullContainerPath = tempMocPathDict.Count != basePathSet.Count;
                foreach (var moc in mocDict.Keys)
                {
                    var mocPath = useFullContainerPath
                        ? tempMocPathDict[moc].Item1 //fullContainerPath
                        : tempMocPathDict[moc].Item2; //basePath
                    if (searchByFilename)
                    {
                        mocPathDict.Add(moc, moc.assetsFile.fullName);
                        if (mocDict.TryGetValue(moc, out var model) && model != null)
                            model.Container = mocPath;
                    }
                    else
                    {
                        mocPathDict.Add(moc, mocPath);
                    }
                }
                tempMocPathDict.Clear();
            }
            return mocPathDict;
        }

        private static void ShowCurProgressValue(int value)
        {
            Console.Write($"[{value:000}%]\r");
        }

        private static void ExitMsg()
        {
            Console.WriteLine("\n\nPress any key to exit");
            Console.ReadKey(intercept: true);
        }
    }
}
