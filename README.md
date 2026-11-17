# Windows 本地日中双语字幕

Windows 11 本地 WPF 字幕台：从用户选择的麦克风或播放设备（WASAPI Loopback）采集音频，使用本机 sherpa-onnx Qwen3-ASR 识别日语，再用 CTranslate2 LMT 直接翻译为简体中文。支持置顶滚动字幕、字幕历史 CSV 追加保存和自制 WAV 验证。音频和文本不会上传，也没有云端回退或遥测。

## 启动

双击 `Start-Captions.cmd`，或在 Windows x64 且已安装 .NET 10 SDK 的环境运行：

```powershell
dotnet run --project .\JapaneseTranscriptionPrototype.csproj
```

应用启动时只枚举播放设备，不自动开始录音或字幕。主窗口的“播放设备”下拉框会明确显示设备名称及 loopback 来源。只有点击“开始双语字幕”后才会采集；Loopback 是设备级采集，可能包含所选输出设备上的 Teams 及其他程序声音，不保证只采集 Teams。

## 本地模型与 Python 环境

- Qwen3-ASR 模型目录需包含 `conv_frontend.onnx`、`encoder.int8.onnx`、`decoder.int8.onnx` 和 `tokenizer` 子目录。
- LMT CTranslate2 模型目录需包含 `model.bin`、`tokenizer.json` 和 `chat_template.jinja`。
- 应用默认指向 `D:\AI\models\asr-qwen3` 与 `D:\AI\models\mt-lmt-high`；可在主界面的模型配置中修改。
- Python 依赖版本列于 `local-model-requirements.txt`。在 Windows 安装 Python 后，可自行创建项目隔离环境并安装：

```powershell
py -m venv .runtime
.\.runtime\Scripts\python.exe -m pip install -r .\local-model-requirements.txt
```

应用优先使用根目录 `.runtime\Scripts\python.exe`。已有的本地工作区隔离环境也会从 `JapaneseTranscriptionPrototype\.runtime` 自动识别。模型权重和依赖不会随仓库发布；程序不自动下载模型/依赖，不执行模型目录内脚本，不启用 `trust_remote_code`，也不联网回退。

官方依赖项目：[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)、[CTranslate2](https://github.com/OpenNMT/CTranslate2)、[LMT](https://github.com/NiuTrans/LMT)、[OpenCC](https://github.com/BYVoid/OpenCC)。

## 验证

运行聚焦的 .NET 测试：

```powershell
dotnet test .\Tests\JapaneseTranscriptionPrototype.Tests.csproj
```

本机模型集成测试仅在显式选择自制/获准音频后手动开启。可用环境变量 `RUN_LOCAL_MODEL_INTEGRATION=1`、`JA1_WAV_PATH`、`LOCAL_MODEL_PYTHON`、`QWEN3_ASR_PATH` 和 `LMT_MODEL_PATH` 指定测试素材及模型目录。测试不启用音频设备；没有准确参考稿时不报告 CER。

## 字幕与历史

- 主窗口和置顶悬浮窗显示时间戳、日语原文和更醒目的简体中文译文；新句追加到时间线。虚拟化列表允许回看历史，向上滚动时暂停自动跟随。
- 跨会话 CSV 保存默认关闭。用户选定文件并明确开启后，字幕以 UTF-8 无 BOM 格式追加；已有文件不会覆盖。记录包含 session start/end、时间戳、日文和中文。
- 设置存放在 `%LocalAppData%\JapaneseTranscriptionPrototype\caption-history-settings.json`；历史文件路径由用户选择。未启用保存时，关闭窗口会提示仍留在内存的记录数量。
- 写入失败会明确显示错误，不会伪称已保存。临时录音只在本机处理并清理；只有用户明确操作才会另存原始 WAV。

不要导入、录制或测试真实客户会议资料。测试实时 loopback 前，请先确认环境中没有其他敏感声音；测试音频只使用获准公开或自制的非敏感素材。识别和翻译仅供辅助，不保证数字、专名、否定或会议内容准确。

## 发布构建

需要 Windows x64 与 .NET 10 SDK。发布目录 `artifacts` 在 `.gitignore` 中，不会提交二进制或模型：

```powershell
dotnet publish .\JapaneseTranscriptionPrototype.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\app-verified
```
