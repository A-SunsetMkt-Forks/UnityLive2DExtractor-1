﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityLive2DExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("Usage: \nUnityLive2DExtractor <path to a Live2D folder>\n");
                return;
            }
            if (!Directory.Exists(args[0]))
            {
                Console.WriteLine($"Invalid input path \"{args[0]}\". \nSpecified folder was not found.\n");
                return;
            }
            Console.WriteLine($"Loading...");
            var assetsManager = new AssetsManager();
            assetsManager.LoadFolder(args[0]);
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
                foreach (var asset in assetsFile.Objects)
                {
                    switch (asset)
                    {
                        case MonoBehaviour m_MonoBehaviour:
                            if (m_MonoBehaviour.m_Script.TryGet(out var m_Script))
                            {
                                if (m_Script.m_ClassName == "CubismMoc")
                                {
                                    cubismMocs.Add(m_MonoBehaviour);
                                }
                            }
                            break;
                        case AssetBundle m_AssetBundle:
                            foreach (var m_Container in m_AssetBundle.m_Container)
                            {
                                var preloadIndex = m_Container.Value.preloadIndex;
                                var preloadSize = m_Container.Value.preloadSize;
                                var preloadEnd = preloadIndex + preloadSize;
                                for (int k = preloadIndex; k < preloadEnd; k++)
                                {
                                    var pptr = m_AssetBundle.m_PreloadTable[k];
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
                    }
                }
            }

            var useFullContainerPath = false;
            if (cubismMocs.Count > 1)
            {
                var basePathSet = cubismMocs.Select(x => containers[x].Substring(0, containers[x].LastIndexOf("/"))).ToHashSet();

                if (basePathSet.Count != cubismMocs.Count)
                {
                    useFullContainerPath = true;
                }
            }
            var basePathList = useFullContainerPath ?
                cubismMocs.Select(x => containers[x]).ToList() : 
                cubismMocs.Select(x => containers[x].Substring(0, containers[x].LastIndexOf("/"))).ToList();
            var lookup = containers.ToLookup(
                x => basePathList.Find(b => x.Value.Contains(b) && x.Value.Split('/').Any(y => y == b.Substring(b.LastIndexOf("/") + 1))),
                x => x.Key
            );
            var totalModelCount = lookup.LongCount(x => x.Key != null);
            Console.WriteLine($"Found {totalModelCount} model(s)");
            var modelCounter = 0;
            var baseDestPath = Path.Combine(Path.GetDirectoryName(args[0]), "Live2DOutput");
            foreach (var assets in lookup)
            {
                var container = assets.Key;
                if (container == null)
                    continue;
                var modelName = useFullContainerPath ? Path.GetFileNameWithoutExtension(container) : container.Substring(container.LastIndexOf("/") + 1);
                container = Path.HasExtension(container) ? container.Replace(Path.GetExtension(container), "") : container;

                Console.Write($"[{++modelCounter}/{totalModelCount}] ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"{container}: ");
                Console.ResetColor();
                Console.WriteLine("Extracting...");

                var destPath = Path.Combine(baseDestPath, container) + Path.DirectorySeparatorChar;
                var destTexturePath = Path.Combine(destPath, "textures") + Path.DirectorySeparatorChar;
                var destMotionPath = Path.Combine(destPath, "motions") + Path.DirectorySeparatorChar;
                var destExpressionPath = Path.Combine(destPath, "expressions") + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(destPath);
                Directory.CreateDirectory(destTexturePath);

                var monoBehaviours = new List<MonoBehaviour>();
                var texture2Ds = new List<Texture2D>();
                var gameObjects = new List<GameObject>();
                var animationClips = new List<AnimationClip>();

                foreach (var asset in assets)
                {
                    switch (asset)
                    {
                        case MonoBehaviour m_MonoBehaviour:
                            monoBehaviours.Add(m_MonoBehaviour);
                            break;
                        case Texture2D m_Texture2D:
                            texture2Ds.Add(m_Texture2D);
                            break;
                        case GameObject m_GameObject:
                            gameObjects.Add(m_GameObject);
                            break;
                        case AnimationClip m_AnimationClip:
                            animationClips.Add(m_AnimationClip);
                            break;
                    }
                }

                //physics
                var physics = monoBehaviours.FirstOrDefault(x =>
                {
                    if (x.m_Script.TryGet(out var m_Script))
                    {
                        return m_Script.m_ClassName == "CubismPhysicsController";
                    }
                    return false;
                });
                if (physics != null)
                {
                    try
                    {
                        var buff = ParsePhysics(physics);
                        File.WriteAllText($"{destPath}{modelName}.physics3.json", buff);
                    }
                    catch
                    {
                        Console.WriteLine($"Error in parsing physics data.\n");
                        physics = null;
                    }
                }

                //moc
                var moc = monoBehaviours.First(x =>
                {
                    if (x.m_Script.TryGet(out var m_Script))
                    {
                        return m_Script.m_ClassName == "CubismMoc";
                    }
                    return false;
                });
                File.WriteAllBytes($"{destPath}{modelName}.moc3", ParseMoc(moc));

                //texture
                var textures = new SortedSet<string>();
                foreach (var texture2D in texture2Ds)
                {
                    using (var image = texture2D.ConvertToImage(flip: true))
                    {
                        textures.Add($"textures/{texture2D.m_Name}.png");
                        using (var file = File.OpenWrite($"{destTexturePath}{texture2D.m_Name}.png"))
                        {
                            image.WriteToStream(file, ImageFormat.Png);
                        }
                    }
                }

                //motion
                var motions = new JArray();
                if (gameObjects.Count > 0)
                {
                    var rootTransform = gameObjects[0].m_Transform;
                    while (rootTransform.m_Father.TryGet(out var m_Father))
                    {
                        rootTransform = m_Father;
                    }
                    rootTransform.m_GameObject.TryGet(out var rootGameObject);
                    var converter = new CubismMotion3Converter(rootGameObject, animationClips.ToArray());
                    if (converter.AnimationList.Count > 0)
                    {
                        Directory.CreateDirectory(destMotionPath);
                    }
                    foreach (ImportedKeyframedAnimation animation in converter.AnimationList)
                    {
                        var json = new CubismMotion3Json
                        {
                            Version = 3,
                            Meta = new CubismMotion3Json.SerializableMeta
                            {
                                Duration = animation.Duration,
                                Fps = animation.SampleRate,
                                Loop = true,
                                AreBeziersRestricted = true,
                                CurveCount = animation.TrackList.Count,
                                UserDataCount = animation.Events.Count
                            },
                            Curves = new CubismMotion3Json.SerializableCurve[animation.TrackList.Count]
                        };
                        int totalSegmentCount = 1;
                        int totalPointCount = 1;
                        for (int i = 0; i < animation.TrackList.Count; i++)
                        {
                            var track = animation.TrackList[i];
                            json.Curves[i] = new CubismMotion3Json.SerializableCurve
                            {
                                Target = track.Target,
                                Id = track.Name,
                                Segments = new List<float> { 0f, track.Curve[0].value }
                            };
                            for (var j = 1; j < track.Curve.Count; j++)
                            {
                                var curve = track.Curve[j];
                                var preCurve = track.Curve[j - 1];
                                if (Math.Abs(curve.time - preCurve.time - 0.01f) < 0.0001f) //InverseSteppedSegment
                                {
                                    var nextCurve = track.Curve[j + 1];
                                    if (nextCurve.value == curve.value)
                                    {
                                        json.Curves[i].Segments.Add(3f);
                                        json.Curves[i].Segments.Add(nextCurve.time);
                                        json.Curves[i].Segments.Add(nextCurve.value);
                                        j += 1;
                                        totalPointCount += 1;
                                        totalSegmentCount++;
                                        continue;
                                    }
                                }
                                if (float.IsPositiveInfinity(curve.inSlope)) //SteppedSegment
                                {
                                    json.Curves[i].Segments.Add(2f);
                                    json.Curves[i].Segments.Add(curve.time);
                                    json.Curves[i].Segments.Add(curve.value);
                                    totalPointCount += 1;
                                }
                                else if (preCurve.outSlope == 0f && Math.Abs(curve.inSlope) < 0.0001f) //LinearSegment
                                {
                                    json.Curves[i].Segments.Add(0f);
                                    json.Curves[i].Segments.Add(curve.time);
                                    json.Curves[i].Segments.Add(curve.value);
                                    totalPointCount += 1;
                                }
                                else //BezierSegment
                                {
                                    var tangentLength = (curve.time - preCurve.time) / 3f;
                                    json.Curves[i].Segments.Add(1f);
                                    json.Curves[i].Segments.Add(preCurve.time + tangentLength);
                                    json.Curves[i].Segments.Add(preCurve.outSlope * tangentLength + preCurve.value);
                                    json.Curves[i].Segments.Add(curve.time - tangentLength);
                                    json.Curves[i].Segments.Add(curve.value - curve.inSlope * tangentLength);
                                    json.Curves[i].Segments.Add(curve.time);
                                    json.Curves[i].Segments.Add(curve.value);
                                    totalPointCount += 3;
                                }
                                totalSegmentCount++;
                            }
                        }
                        json.Meta.TotalSegmentCount = totalSegmentCount;
                        json.Meta.TotalPointCount = totalPointCount;

                        json.UserData = new CubismMotion3Json.SerializableUserData[animation.Events.Count];
                        var totalUserDataSize = 0;
                        for (var i = 0; i < animation.Events.Count; i++)
                        {
                            var @event = animation.Events[i];
                            json.UserData[i] = new CubismMotion3Json.SerializableUserData
                            {
                                Time = @event.time,
                                Value = @event.value
                            };
                            totalUserDataSize += @event.value.Length;
                        }
                        json.Meta.TotalUserDataSize = totalUserDataSize;

                        motions.Add(new JObject
                        {
                            { "Name", animation.Name },
                            { "File", $"motions/{animation.Name}.motion3.json" }
                        });
                        File.WriteAllText($"{destMotionPath}{animation.Name}.motion3.json", JsonConvert.SerializeObject(json, Formatting.Indented, new MyJsonConverter()));
                    }
                }

                //expression
                var expressions = new JArray();
                var monoBehaviourArray = monoBehaviours.Where(x => x.m_Name.EndsWith(".exp3")).ToArray();
                if (monoBehaviourArray.Length > 0)
                {
                    Directory.CreateDirectory(destExpressionPath);
                }
                foreach (var monoBehaviour in monoBehaviourArray)
                {
                    var fullName = monoBehaviour.m_Name;
                    var expressionName = fullName.Replace(".exp3", "");
                    var expressionObj = monoBehaviour.ToType();
                    if (expressionObj == null)
                        continue;
                    var expression = JsonConvert.DeserializeObject<CubismExpression3Json>(JsonConvert.SerializeObject(expressionObj));

                    expressions.Add(new JObject
                    {
                        { "Name", expressionName },
                        { "File", $"expressions/{fullName}.json" }
                    });
                    File.WriteAllText($"{destExpressionPath}{fullName}.json", JsonConvert.SerializeObject(expression, Formatting.Indented));
                }

                //model
                var groups = new List<CubismModel3Json.SerializableGroup>();

                var eyeBlinkParameters = monoBehaviours.Where(x =>
                {
                    x.m_Script.TryGet(out var m_Script);
                    return m_Script?.m_ClassName == "CubismEyeBlinkParameter";
                }).Select(x =>
                {
                    x.m_GameObject.TryGet(out var m_GameObject);
                    return m_GameObject?.m_Name;
                }).ToHashSet();
                if (eyeBlinkParameters.Count == 0)
                {
                    eyeBlinkParameters = gameObjects.Where(x =>
                    {
                        return x.m_Name.ToLower().Contains("eye") 
                        && x.m_Name.ToLower().Contains("open") 
                        && (x.m_Name.ToLower().Contains('l') || x.m_Name.ToLower().Contains('r'));
                    }).Select(x => x.m_Name).ToHashSet();
                }                
                groups.Add(new CubismModel3Json.SerializableGroup
                {
                    Target = "Parameter",
                    Name = "EyeBlink",
                    Ids = eyeBlinkParameters.ToArray()
                });

                var lipSyncParameters = monoBehaviours.Where(x =>
                {
                    x.m_Script.TryGet(out var m_Script);
                    return m_Script?.m_ClassName == "CubismMouthParameter";
                }).Select(x =>
                {
                    x.m_GameObject.TryGet(out var m_GameObject);
                    return m_GameObject?.m_Name;
                }).ToHashSet();
                if (lipSyncParameters.Count == 0)
                {
                    lipSyncParameters = gameObjects.Where(x =>
                    {
                        return x.m_Name.ToLower().Contains("mouth") 
                        && x.m_Name.ToLower().Contains("open") 
                        && x.m_Name.ToLower().Contains('y');
                    }).Select(x => x.m_Name).ToHashSet();
                }
                groups.Add(new CubismModel3Json.SerializableGroup
                {
                    Target = "Parameter",
                    Name = "LipSync",
                    Ids = lipSyncParameters.ToArray()
                });

                var model3 = new CubismModel3Json
                {
                    Version = 3,
                    Name = modelName,
                    FileReferences = new CubismModel3Json.SerializableFileReferences
                    {
                        Moc = $"{modelName}.moc3",
                        Textures = textures.ToArray(),
                        Motions = new JObject { { "", motions } },
                        Expressions = expressions,
                    },
                    Groups = groups.ToArray()
                };
                if (physics != null)
                {
                    model3.FileReferences.Physics = $"{modelName}.physics3.json";
                }
                File.WriteAllText($"{destPath}{modelName}.model3.json", JsonConvert.SerializeObject(model3, Formatting.Indented));
            }

            Console.Write($"\nFinished extracting to the ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"\"{Path.GetFullPath(baseDestPath)}\" ");
            Console.ResetColor();
            Console.WriteLine("folder.");

            Console.Write("\nPress any key to exit\r");
            Console.ReadKey(intercept: true);
        }

        private static string ParsePhysics(MonoBehaviour physics)
        {
            var reader = physics.reader;
            reader.Reset();
            reader.Position += 28; //PPtr<GameObject> m_GameObject, m_Enabled, PPtr<MonoScript>
            reader.ReadAlignedString(); //m_Name
            var cubismPhysicsRig = new CubismPhysicsRig(reader);

            var physicsSettings = new CubismPhysics3Json.SerializablePhysicsSettings[cubismPhysicsRig.SubRigs.Length];
            for (int i = 0; i < physicsSettings.Length; i++)
            {
                var subRigs = cubismPhysicsRig.SubRigs[i];
                physicsSettings[i] = new CubismPhysics3Json.SerializablePhysicsSettings
                {
                    Id = $"PhysicsSetting{i + 1}",
                    Input = new CubismPhysics3Json.SerializableInput[subRigs.Input.Length],
                    Output = new CubismPhysics3Json.SerializableOutput[subRigs.Output.Length],
                    Vertices = new CubismPhysics3Json.SerializableVertex[subRigs.Particles.Length],
                    Normalization = new CubismPhysics3Json.SerializableNormalization
                    {
                        Position = new CubismPhysics3Json.SerializableNormalizationValue
                        {
                            Minimum = subRigs.Normalization.Position.Minimum,
                            Default = subRigs.Normalization.Position.Default,
                            Maximum = subRigs.Normalization.Position.Maximum
                        },
                        Angle = new CubismPhysics3Json.SerializableNormalizationValue
                        {
                            Minimum = subRigs.Normalization.Angle.Minimum,
                            Default = subRigs.Normalization.Angle.Default,
                            Maximum = subRigs.Normalization.Angle.Maximum
                        }
                    }
                };
                for (int j = 0; j < subRigs.Input.Length; j++)
                {
                    var input = subRigs.Input[j];
                    physicsSettings[i].Input[j] = new CubismPhysics3Json.SerializableInput
                    {
                        Source = new CubismPhysics3Json.SerializableParameter
                        {
                            Target = "Parameter", //同名GameObject父节点的名称
                            Id = input.SourceId
                        },
                        Weight = input.Weight,
                        Type = Enum.GetName(typeof(CubismPhysicsSourceComponent), input.SourceComponent),
                        Reflect = input.IsInverted
                    };
                }
                for (int j = 0; j < subRigs.Output.Length; j++)
                {
                    var output = subRigs.Output[j];
                    physicsSettings[i].Output[j] = new CubismPhysics3Json.SerializableOutput
                    {
                        Destination = new CubismPhysics3Json.SerializableParameter
                        {
                            Target = "Parameter", //同名GameObject父节点的名称
                            Id = output.DestinationId
                        },
                        VertexIndex = output.ParticleIndex,
                        Scale = output.AngleScale,
                        Weight = output.Weight,
                        Type = Enum.GetName(typeof(CubismPhysicsSourceComponent), output.SourceComponent),
                        Reflect = output.IsInverted
                    };
                }
                for (int j = 0; j < subRigs.Particles.Length; j++)
                {
                    var particles = subRigs.Particles[j];
                    physicsSettings[i].Vertices[j] = new CubismPhysics3Json.SerializableVertex
                    {
                        Position = new CubismPhysics3Json.SerializableVector2
                        {
                            X = particles.InitialPosition.X,
                            Y = particles.InitialPosition.Y
                        },
                        Mobility = particles.Mobility,
                        Delay = particles.Delay,
                        Acceleration = particles.Acceleration,
                        Radius = particles.Radius
                    };
                }
            }
            var physicsDictionary = new CubismPhysics3Json.SerializablePhysicsDictionary[physicsSettings.Length];
            for (int i = 0; i < physicsSettings.Length; i++)
            {
                physicsDictionary[i] = new CubismPhysics3Json.SerializablePhysicsDictionary
                {
                    Id = $"PhysicsSetting{i + 1}",
                    Name = $"Dummy{i + 1}"
                };
            }
            var physicsJson = new CubismPhysics3Json
            {
                Version = 3,
                Meta = new CubismPhysics3Json.SerializableMeta
                {
                    PhysicsSettingCount = cubismPhysicsRig.SubRigs.Length,
                    TotalInputCount = cubismPhysicsRig.SubRigs.Sum(x => x.Input.Length),
                    TotalOutputCount = cubismPhysicsRig.SubRigs.Sum(x => x.Output.Length),
                    VertexCount = cubismPhysicsRig.SubRigs.Sum(x => x.Particles.Length),
                    EffectiveForces = new CubismPhysics3Json.SerializableEffectiveForces
                    {
                        Gravity = new CubismPhysics3Json.SerializableVector2
                        {
                            X = 0,
                            Y = -1
                        },
                        Wind = new CubismPhysics3Json.SerializableVector2
                        {
                            X = 0,
                            Y = 0
                        }
                    },
                    PhysicsDictionary = physicsDictionary
                },
                PhysicsSettings = physicsSettings
            };
            return JsonConvert.SerializeObject(physicsJson, Formatting.Indented, new MyJsonConverter2());
        }

        private static byte[] ParseMoc(MonoBehaviour moc)
        {
            var reader = moc.reader;
            reader.Reset();
            reader.Position += 28; //PPtr<GameObject> m_GameObject, m_Enabled, PPtr<MonoScript>
            reader.ReadAlignedString(); //m_Name
            return reader.ReadBytes(reader.ReadInt32());
        }
    }
}
