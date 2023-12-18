# UnityLive2DExtractor
用于从Unity AssetBundle中提取Live2D Cubism 3文件

Used to extract Live2D Cubism 3 files from Unity AssetBundle

## Additional features
UnityLive2DExtractor has been integrated into [AssetStudioMod](https://github.com/aelurum/AssetStudio) since version 0.17.1 (including the CLI version)

Using the Live2D export option in AssetStudioMod makes possible such features as:
- specifying a Unity version
- specifying an assembly folder
- specifying default motion export mode
- enabling calculation of Linear motion segments as Bezier segments (may help if the exported motions look jerky/not smooth enough)

## Usage
拖放Live2D文件夹到exe上，多个Live2D文件请放到一个文件夹内，将在文件夹所在目录生成`Live2DOutput`目录

Drag and drop the Live2D folder to the executable file or use the command-line. Please put multiple Live2D files into a folder, and the `Live2DOutput` directory will be generated in the directory where the folder is located

## Command-line
```bash
UnityLive2DExtractor <live2dfolder>
```

## Command-line for Portable ver (.NET 6+)
```bash
dotnet UnityLive2DExtractor.dll <live2dfolder>
```

## Requirements
- [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472) / [.NET Runtime 6](https://dotnet.microsoft.com/download/dotnet/6.0) / [.NET Runtime 7](https://dotnet.microsoft.com/download/dotnet/7.0) / [.NET Runtime 8](https://dotnet.microsoft.com/download/dotnet/8.0)
