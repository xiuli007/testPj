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

分句测试使用内存中的合成正弦波、静音和低能量脉冲，覆盖长语音、短停顿、安静与时长上限；清除测试只写临时测试 CSV，不启动 UI、不读取用户历史设置或采集音频。

## 按停顿动态分句

- 实时字幕在本地将 16kHz 单声道音频按 20ms 帧复用现有能量门限检测，不再每 6 秒强行切片。连续约 800ms 低能量后，整段送入日语识别再翻译；短停顿继续缓冲，尽量减少句中截断。
- 安静时只保留最多 200ms 前置音频，不产生识别任务；设备停止提供数据约 1 秒后（400ms 定时检查）提交已有尾段。仍沿用至少 0.5 秒及整段能量过滤。
- 每段最多 20 秒（含前置音频及停顿）；达到上限会强制提交并显示提示。等待队列仍最多两段（最多约 40 秒音频，另有当前处理段和采集段），处理跟不上时丢弃新段并显示提示，不无限堆积。
- 这些是固定、本地可控的延迟与截句折中，不是语义断句或精准 VAD：噪声/音乐可能被视为语音，低声可能被过滤，句中长停顿或连续语音上限仍可能截句。停顿等待、排队和模型推理都会增加字幕延迟。
- “停止字幕”和退出仍取消当前推理，丢弃未处理队列及未提交尾段，不为等句末无限等待；临时音频会清理，设备/推理/清理错误会显示。

## 字幕与历史

- 主窗口和置顶悬浮窗显示时间戳、日语原文和更醒目的简体中文译文；新句追加到时间线。虚拟化列表允许回看历史，向上滚动时暂停自动跟随。
- 两窗均有“清除字幕”按钮，同步清空共享时间线和最新双语显示，并恢复新句自动跟随；只影响界面，不停止采集或识别、不修改 CSV、保存设置或内存中的待写记录。清除前正在处理的句子完成后和后续新句一样继续追加；启用/恢复历史保存后，已从界面清除的待写记录仍会写入 CSV。
- 跨会话 CSV 保存默认关闭。用户选定文件并明确开启后，字幕以 UTF-8 无 BOM 格式追加；已有文件不会覆盖。记录包含 session start/end、时间戳、日文和中文。
- 设置存放在 `%LocalAppData%\JapaneseTranscriptionPrototype\caption-history-settings.json`；历史文件路径由用户选择。未启用保存时，关闭窗口会提示仍留在内存的记录数量。
- 写入失败会明确显示错误，不会伪称已保存。临时录音只在本机处理并清理；只有用户明确操作才会另存原始 WAV。

不要导入、录制或测试真实客户会议资料。测试实时 loopback 前，请先确认环境中没有其他敏感声音；测试音频只使用获准公开或自制的非敏感素材。识别和翻译仅供辅助，不保证数字、专名、否定或会议内容准确。

## 发布构建

需要 Windows x64 与 .NET 10 SDK。发布目录 `artifacts` 在 `.gitignore` 中，不会提交二进制或模型：

```powershell
dotnet publish .\JapaneseTranscriptionPrototype.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\app-verified
```
