# Chill With You · DeepSeek 外观 Mod

为《Chill with You : Lo-Fi Story》替换游戏角色外观的 Windows BepInEx Mod。它使用游戏原有的角色动画与 lilToon 着色器，并支持眼睛和眉毛以 50% 不透明度透过头发显示。

## 安装

1. 为游戏安装 BepInEx 5。
2. 从仓库的 Releases 下载 Mod 压缩包，将其中的 `ChillWithAnyone` 文件夹放到游戏的 `BepInEx/plugins/`。
3. 启动游戏进入房间。

## 构建

在仓库根目录运行 `dotnet build src/Cavi.ChillWithAnyone.csproj -c Release -p:GamePath="<游戏安装目录>"`。构建使用本机游戏和 BepInEx 的程序集。

用 Unity 2022.3.62f3 打开 `DeepSeekUnity`，执行菜单 **DeepSeek → Import Blender Data and Build Native Bundle**。仓库附带 Blender 导出的网格、骨架绑定和贴图数据，构建结果为 `DeepSeekUnity/Build/NativeShader/assets`。安装时将该文件、插件 DLL 和 `config.txt` 放在同一插件目录。

## 署名与许可

插件基于 Cavibot 的 Chill With Anyone v1.0。此版本增加了 DeepSeek 模型适配、游戏原生材质复用和透发渲染。代码沿用上游 MIT 许可，见 [LICENSE](LICENSE) 和 [上游项目](https://github.com/Cavibot/chill-lofi-appearance-mod/tree/v1.0)。

DeepSeek 角色形象及本 Mod 中由该形象改编的网格和贴图按 [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/deed.zh-hans) 提供。游戏原有内容的权利归各自权利人。
