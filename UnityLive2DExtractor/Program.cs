using AssetStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnityLive2DExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            var appAssembly = typeof(Program).Assembly.GetName();
            var arch = Environment.Is64BitProcess ? "x64" : "x32";
            Console.Title = $"{appAssembly.Name} v{appAssembly.Version} [{arch}]";

            if (args.Length != 1)
            {
                Console.WriteLine("Usage: \nUnityLive2DExtractor <path to a Live2D folder>\n");
                return;
            }
            if (!Directory.Exists(args[0]))
            {
                Console.WriteLine($"{"[Error]".Color(ColorConsole.BrightRed)} Invalid input path \"{args[0]}\". \nSpecified folder was not found.\n");
                return;
            }
            Progress.Default = new Progress<int>(ShowCurProgressValue);
            var motionMode = Live2DMotionMode.MonoBehaviour;
            var forceBezier = false;

            Console.WriteLine($"Loading...");
            var assetsManager = new AssetsManager();
            assetsManager.SetAssetFilter(
                ClassIDType.AnimationClip,
                ClassIDType.GameObject,
                ClassIDType.MonoBehaviour,
                ClassIDType.Texture2D,
                ClassIDType.Transform
            );
            assetsManager.LoadFilesAndFolders(args[0]);
            if (assetsManager.assetsFileList.Count == 0)
            {
                Console.WriteLine("No Unity file can be loaded.\n");
                return;
            }
            if (args[0].EndsWith($"{Path.DirectorySeparatorChar}"))
                args[0] = args[0].Remove(args[0].Length - 1);

            var containers = new Dictionary<AssetStudio.Object, string>();
            var cubismMocs = new List<MonoBehaviour>();
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                var preloadTable = Array.Empty<PPtr<AssetStudio.Object>>();
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
                                var preloadSize = isStreamedSceneAssetBundle ? preloadTable.Length : m_Container.Value.preloadSize;
                                var preloadEnd = preloadIndex + preloadSize;
                                for (var k = preloadIndex; k < preloadEnd; k++)
                                {
                                    var pptr = preloadTable[k];
                                    if (pptr.TryGet(out var obj))
                                    {
                                        containers[obj] = m_Container.Key;
                                    }
                                }
                            }
                            break;
                        case ResourceManager m_ResourceManager:
                            foreach (var m_Container in m_ResourceManager.m_Container)
                            {
                                if (m_Container.Value.TryGet(out var obj))
                                {
                                    containers[obj] = m_Container.Key;
                                }
                            }
                            break;
                        case MonoBehaviour m_MonoBehaviour:
                            if (m_MonoBehaviour.m_Script.TryGet(out var m_Script))
                            {
                                if (m_Script.m_ClassName == "CubismMoc")
                                {
                                    cubismMocs.Add(m_MonoBehaviour);
                                }
                            }
                            break;
                    }
                }
            }
            if (cubismMocs.Count == 0)
            {
                Console.WriteLine("Live2D Cubism models were not found.\n");
                return;
            }

            Progress.Reset();
            Console.WriteLine("Searching for Live2D files...");
            var useFullContainerPath = false;
            if (cubismMocs.Count > 1) //autodetection of identical base paths
            {
                var basePathSet = cubismMocs.Select(x =>
                {
                    var pathLen = containers.TryGetValue(x, out var itemContainer) ? itemContainer.LastIndexOf("/") : 0;
                    pathLen = pathLen < 0 ? containers[x].Length : pathLen;
                    return itemContainer?.Substring(0, pathLen);
                }).ToHashSet();

                if (basePathSet.All(x => x == null))
                {
                    Console.WriteLine( $"{"[Error]".Color(ColorConsole.BrightRed)} Live2D Cubism export error: Cannot find any model related files.");
                    return;
                }

                if (basePathSet.Count != cubismMocs.Count)
                {
                    useFullContainerPath = true;
                }
            }
            var basePathList = cubismMocs.Select(x =>
            {
                containers.TryGetValue(x, out var container);
                container = useFullContainerPath
                    ? container
                    : container?.Substring(0, container.LastIndexOf("/"));
                return container;
            }).Where(x => x != null).ToList();

            var lookup = containers.ToLookup(
                x => basePathList.Find(b => x.Value.Contains(b) && x.Value.Split('/').Any(y => y == b.Substring(b.LastIndexOf("/") + 1))),
                x => x.Key
            );

            var totalModelCount = lookup.LongCount(x => x.Key != null);
            var baseDestPath = Path.Combine(Path.GetDirectoryName(args[0]), "Live2DOutput");
            Console.WriteLine($"Found {totalModelCount} model(s)");
            var modelCounter = 0;
            foreach (var modelAssets in lookup)
            {
                var srcContainer = modelAssets.Key;
                if (srcContainer == null)
                    continue;
                var container = srcContainer;

                Console.WriteLine($"[{modelCounter + 1}/{totalModelCount}] Extracting Live2D: \"{srcContainer.Color(ColorConsole.BrightCyan)}\"");

                try
                {
                    var modelName = useFullContainerPath ? Path.GetFileNameWithoutExtension(container) : container.Substring(container.LastIndexOf('/') + 1);
                    container = Path.HasExtension(container) ? container.Replace(Path.GetExtension(container), "") : container;
                    var destPath = Path.Combine(baseDestPath, container) + Path.DirectorySeparatorChar;

                    ModelExtractor.ExtractLive2D(modelAssets, destPath, modelName, motionMode, forceBezier);
                    modelCounter++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{"[Error]".Color(ColorConsole.BrightRed)} Live2D model export error: \"{srcContainer}\"", ex);
                }
            }
            var status = modelCounter > 0 ?
                $"Finished extracting [{modelCounter}/{totalModelCount}] Live2D model(s) to \"{Path.GetFullPath(baseDestPath).Color(ColorConsole.BrightCyan)}\"" :
                "Nothing extracted.";
            Console.WriteLine(status);
            Console.Write("\nPress any key to exit\r");
            Console.ReadKey(intercept: true);
        }

        private static void ShowCurProgressValue(int value)
        {
            Console.Write($"[{value:000}%]\r");
        }
    }
}
