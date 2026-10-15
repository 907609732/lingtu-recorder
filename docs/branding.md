# 灵兔拆包智录品牌素材

## 最终选择

用户确认采用最小品牌调整：保留原软件 Logo 的浅蓝圆角底、纸箱与右上摄像头构图，仅在右下角加入灵兔主品牌小徽章。软件既有界面风格、布局与业务内容保持现状，不以本次 Logo 更新为由重新设计界面。

产品名为「灵兔拆包智录」，品牌署名为「灵兔 LTTLT」。Logo 图片只承载图形；完整产品名使用应用或网站的正常文字排版，方便阅读、搜索与无障碍访问。

灵兔主品牌参考来自用户提供的[钉钉 Logo 文档](https://alidocs.dingtalk.com/i/nodes/OG9lyrgJPz5znEApSPmxMmjaWzN67Mw4)。原软件图形来自本仓库既有 `src/UnpackVision.App/Assets/EcommerceUnpackRecorder-Logo.png`，已在覆盖派生资产前保留备份。

## 生成与处理记录

- 生成工具：built-in ImageGen，采用已有软件 Logo 的局部编辑；未使用 API/CLI 替代生成。
- 提示词意图：保留已有浅蓝圆角软件图标、纸箱、摄像头、光照与构图，在右下角添加小型白底圆角灵兔徽章；徽章中的跃兔沿用主品牌蓝青渐变；不添加文字，不改成全新的兔子取景框图标。
- 原始生成文件：`design/brand/LingtuRecorder-Logo-2.6.0-Source.png`，1254×1254 RGBA，保持生成文件原始字节和透明度。
- 明确遮罩：旧软件 Logo 的 alpha 轮廓，先等比缩放至 1254×1254，再与生成图原有 alpha 相乘。只限制透明范围，不按颜色抠除，不重绘图形。
- 派生母版：`design/brand/LingtuRecorder-Logo-2.6.0-Masked.png`，用于全部尺寸导出。默认脚本直接保留 RGBA；只有传入 `-AlphaMask` 时才应用明确遮罩。
- 历史的 `EcommerceUnpackRecorder-Logo-Original.png`、`EcommerceUnpackRecorder-Logo-Source.png` 和 `UnpackVision-Logo-Apple-2026-Source.png` 均保留。

## 使用原则

- 保留既有软件图形的识别度，让右下徽章表达灵兔归属；不要把小徽章放大成独立于产品图形的主视觉。
- 主品牌使用蓝青渐变；分享画布使用白色与浅蓝底，文字使用深蓝与灰蓝。Android 自适应图标背景继续沿用现有 `#F3F6FA`。
- 录像、警告、错误等业务状态色与本次品牌素材独立，保持原有界面表达。
- 白底分享封面的标题是「灵兔拆包智录」，副标题是「让每一次拆包，都有证据。」；字体为 Windows 系统的 Microsoft YaHei UI。
- 不把原始生成稿直接当作运行时图标：原稿外缘含生成杂点，运行时只使用经明确遮罩处理的派生素材。

## 素材清单

| 路径 | 尺寸 / 内容 | 用途 |
| --- | --- | --- |
| `design/brand/legacy-2.5.2/EcommerceUnpackRecorder-Logo.png` | 原 1024×1024 PNG | 旧版备份与 alpha 遮罩来源 |
| `design/brand/legacy-2.5.2/EcommerceUnpackRecorder.ico` | 原 ICO，原始字节 | 旧版备份 |
| `design/brand/LingtuRecorder-Logo-2.6.0-Source.png` | 1254×1254 RGBA | ImageGen 原稿 |
| `design/brand/LingtuRecorder-Logo-2.6.0-Masked.png` | 1254×1254 RGBA | 导出母版 |
| `src/UnpackVision.App/Assets/EcommerceUnpackRecorder-Logo.png` | 1024×1024 RGBA | WPF 图形 |
| `src/UnpackVision.App/Assets/EcommerceUnpackRecorder.ico` | 16、24、32、48、64、128、256 px，32 位 PNG 条目 | Windows 应用与安装器 |
| `docs/assets/logo.png` | 512×512 RGBA | README |
| `mobile/UnpackVision.Android/app/src/main/res/mipmap-{mdpi,hdpi,xhdpi,xxhdpi,xxxhdpi}/ic_launcher.png` | 48、72、96、144、192 px | Android 传统启动图标 |
| 同目录 `ic_launcher_round.png` | 同上 | Android 传统圆形图标入口 |
| `mobile/UnpackVision.Android/app/src/main/res/drawable-nodpi/ic_launcher_foreground.png` | 432×432 透明画布，图形位于中心 186×186 方框 | Android 自适应前景 |
| `website/public/brand-mark.png` | 256×256 RGBA | 官网小图标 |
| `website/public/unpackvision-logo.png` | 1024×1024 RGBA | 官网产品 Logo |
| `website/public/favicon-32.png` | 32×32 RGBA | 浏览器标签图标 |
| `website/public/apple-touch-icon.png` | 180×180 RGBA | 收藏与主屏图标 |
| `website/public/og.png` | 1200×630，不透明白蓝画布 | 网站分享封面 |
| `design/brand/qa-2.6.0/logo-white.png` | 1024×1024 白底 | 透明边缘检查 |
| `design/brand/qa-2.6.0/android-circle-white.png` | 432×432 白底 | 自适应圆形遮罩预览 |
| `design/brand/qa-2.6.0/android-rounded-square-white.png` | 432×432 白底 | 自适应圆角方形遮罩预览 |

为兼容已有资源引用，本次保留运行时文件名中的 `EcommerceUnpackRecorder` 和 `unpackvision`；这些技术文件名不作为面向用户的品牌名。

## 复现与检查

在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\generate-brand-assets.ps1 `
  -Source .\design\brand\LingtuRecorder-Logo-2.6.0-Source.png `
  -AlphaMask .\design\brand\legacy-2.5.2\EcommerceUnpackRecorder-Logo.png `
  -ProcessedSourceDestination .\design\brand\LingtuRecorder-Logo-2.6.0-Masked.png
```

本次导出检查结果：

- 原始生成稿和保存稿 SHA-256 相同：`c3ea59578beca0e8da45f75acb6705ab1217ab10cd236caa42dcf3b566a56e02`。
- 旧版 PNG 备份 SHA-256：`12882e8d166be8d84091ff57bf4c8970d3d281461a28c55921b2c26ae9cdd98c`；ICO 备份 SHA-256：`259a40007ed36d239d35f5a2481399b92ef2d3b6fb3af33e758ecd10d99e2fd4`。
- 导出母版 SHA-256：`d3f5d629c9a79cfd08a2be3b3f4654be3705bfb00926355b9d44d085bcb4f5da`。
- 母版 alpha 包含 495,509 个完全透明像素、1,077,004 个部分透明像素和 3 个完全不透明像素；生成器原有半透明像素得以保留。
- 旧轮廓之外的非透明像素为 0；遮罩移除了原稿轮廓外 106,277 个非透明杂点像素。白底预览已检查，初次导出的底边蓝黑杂边在保留源 alpha 的交集处理后消除。
- ICO 解析为 7 个有效条目，尺寸为 16、24、32、48、64、128、256 px。
- Android 前景在半径 132 px 的中心安全圆之外有 0 个非透明像素，圆形和圆角方形遮罩预览均保留纸箱、摄像头和灵兔徽章。此项是素材遮罩验证，尚不等同于启动器实机验收。
- 网站分享图尺寸与指定中文标题、口号已目视检查；软件运行时和安装器显示效果由对应发布验收记录另行确认。
