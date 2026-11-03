# 猫咪应用图标

生成日期：2026-10-20。使用 imagegen 技能及内置 image_gen 工具生成，无外部参考图片。

- AppIcon.png：保留透明背景的原始生成图，作为主界面标识及后续编辑源。
- AppIcon.ico：Windows 多尺寸图标，含 16、24、32、48、64、128、256 像素，接入 EXE、任务栏和窗口标题栏。
- 使用 scripts/Convert-AppIcon.ps1 从 PNG 转换 ICO，保留透明度；仅转换格式和尺寸，不重绘。

## 最终生成提示词

```text
Use case: logo-brand. Asset type: a finished Windows desktop application icon for an SSH remote-control app named Clash 遥控台. Primary request: an original friendly cartoon cat mascot, clean and immediately readable at 16, 32, 48 and 256 pixels. Composition: one centered front-facing ivory cat head with two pointed ears, confident friendly navy eyes, a tiny nose and simple smile, slightly rounded cheek silhouette. A simple rounded-square indigo-purple badge behind the cat, matching an existing navy-and-violet desktop UI. Flat polished illustration, bold simple shapes, restrained shading, crisp edges, generous safe padding, square canvas. Actual transparent background outside the rounded-square badge. No text, no letters, no watermark, no mockup, no extra characters, no fine decorative detail. Render a single icon only.
```
