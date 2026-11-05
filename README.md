# nPlayer 视频服务

把 Windows 上的文件夹用 **WebDAV** 或 **FTP** 共享出来，供手机 nPlayer、其他电脑、电视盒子读取播放。
只读服务，不做上传和删除；支持 HTTP Range，38 GB 的 Remux 也能直接拖进度条。

- 运行环境：Windows 10 / 11，需要 .NET 8 运行时（桌面运行时或控制台运行时均可）
- 界面：WPF，单窗口 + 系统托盘常驻
- 发布形态：单文件 exe，约 1 MB，**免安装、可整体拷贝**

---

## 一、快速开始

1. 双击 `NPlayerServer.exe`
2. 勾选协议（默认 WebDAV），确认端口、共享目录
3. 点 **启动服务**

启动后界面会显示可直接粘贴的地址，例如：

```
WebDAV   http://192.168.2.2:8000/
FTP      ftp://192.168.2.2:21
```

**手机 nPlayer 添加方式**：网络 → 添加 → WebDAV → 填地址、端口 8000，用户名密码按配置填（密码留空则选匿名）。

---

## 二、目录结构

### 发布目录（`publish\`）

```
publish\
├─ NPlayerServer.exe   主程序（单文件）
├─ config.json         配置文件（首次运行自动生成）
├─ README.md           本文档
└─ NPlayerServer.pdb   调试符号，可删
```

整个 `publish` 文件夹可以直接拷到别的机器或 U 盘运行，配置跟着走。

### 源码目录

```
NPlayerServer\
├─ NPlayerServer.csproj     项目文件（WPF + WinForms 托盘）
├─ App.xaml / App.xaml.cs    应用入口、样式资源、单实例互斥
├─ MainWindow.xaml / .cs     主界面与业务编排
├─ Models\
│  └─ AppConfig.cs           配置模型
├─ Storage\
│  └─ ConfigStore.cs         JSON 读写 + ACL 收紧
├─ Services\
│  ├─ WebDavServer.cs        WebDAV 服务（自建 HTTP/1.1，TcpListener）
│  ├─ FtpServer.cs           FTP 服务（PASV/EPSV、REST 续传）
│  ├─ ShareResolver.cs       虚拟路径 → 本地路径映射，含穿越防护
│  ├─ NetworkHelper.cs       本机 IPv4 枚举（过滤虚拟网卡）
│  └─ LogBuffer.cs           1000 条环形日志缓冲
├─ tools\make_icon.py        生成 ico 的脚本（纯标准库，无需 PIL）
├─ app.ico                   程序图标
└─ icon\                     各尺寸 PNG 源图
```

---

## 三、配置文件

**位置：exe 同目录的 `config.json`**（相对程序目录，不写系统目录、不写注册表）。

删掉它再启动即恢复默认。当前默认值：

```json
{
  "port": 8000,
  "bindAddress": "0.0.0.0",
  "shares": [
    { "path": "F:\\夸克下载", "alias": "Videos" }
  ],
  "username": "user",
  "password": "",
  "minimizeToTray": true,
  "autoStart": false,
  "enableWebDav": true,
  "enableFtp": false,
  "ftpPort": 21
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `port` | int | WebDAV 端口，默认 8000 |
| `ftpPort` | int | FTP 端口，默认 21。被占用时改成 2121 等 |
| `bindAddress` | string | `0.0.0.0` 表示所有网卡；也可填单个 IP，仅该网卡可见 |
| `shares` | array | 共享目录列表，可多个 |
| `shares[].path` | string | 本地绝对路径，反斜杠要写成 `\\` |
| `shares[].alias` | string |  URL 里显示的名字（虚拟目录名），访问形如 `/Videos/xxx.mkv` |
| `username` | string | 登录用户名，仅密码非空时校验 |
| `password` | string | **明文保存**。留空 = 允许匿名访问 |
| `minimizeToTray` | bool | 关窗口时缩到托盘而不是退出 |
| `autoStart` | bool | 预留字段，当前界面未接线 |
| `enableWebDav` / `enableFtp` | bool | 协议开关，至少开一个才能启动 |

**写入时机**：点「启动服务」时、关闭窗口时，按界面当前状态整体回写。改了界面没点启动就强杀进程，改动不保存。

**相对路径**：程序只按 exe 所在目录找 `config.json`，不受工作目录影响。从快捷方式、计划任务、其他目录用绝对路径调用，读写的都是同一个文件。

---

## 四、命令行参数

```bat
NPlayerServer.exe --start
```

启动后立即拉起服务，不用再点按钮。窗口仍会显示，配合托盘使用。

---

## 五、防火墙

首次运行 exe 时，Windows 会弹窗询问是否允许网络访问，选 **允许**。
允许后会生成按**程序**放行的入站规则（而非按端口），因此 FTP 的随机数据端口也一并覆盖，不需要额外开端口。

如果误点了「取消」，去 `控制面板 → Windows Defender 防火墙 → 允许应用通过防火墙`，找到 NPlayerServer 勾上「专用」即可。

---

## 六、常见问题

**手机连不上**
- 确认手机和电脑在同一网段（同一个小米路由器/交换机下）
- 电脑网络类型必须是「专用网络」，公用网络下系统会拦入站
- 先用手机浏览器打开 `http://电脑IP:8000/`，能出 XML 或提示登录就说明通了
- 电脑 IP 变了：界面右下会显示当前 IP，路由器里给电脑绑个静态 DHCP 更省事

**能连上但拖进度条卡住 / 一直转圈**
- 确认用的是 WebDAV，不是 FTP——部分 FTP 客户端不支持 `REST` 续传
- 播放器缓存调大一些；大码率 Remux 建议有线连接
- 同时多个客户端读同一个文件会抢磁盘 IO，属正常现象

**文件名是中文，FTP 里显示乱码**
- 默认按系统 ANSI（GBK）发给客户端，兼容 Windows 资源管理器
- FileZilla / WinSCP / nPlayer 会自动发 `OPTS UTF8 ON`，切换成 UTF-8，原名完整

**夸克下载的目录名带 `📕`，资源管理器里进不去**
- 已处理：服务器对 ANSI 客户端做了宽松匹配，把无法编码的字符和 `?` 都忽略后比对
- 资源管理器会把它显示成 `低??智??商`，**但能正常进入和播放**

**端口被占用**
- 21 端口常见冲突：IIS 的 FTP 服务、其他 FTP 软件。改 `ftpPort` 或停掉冲突服务
- 8000 端口冲突时改 `port`；改完记得同步改播放器里的地址

**配置文件改了不生效**
- 程序启动时读一次配置。改完要重启程序，不是在界面点启动

**提示「配置保存失败：程序所在目录不可写」**
- 程序被放到了 `C:\Program Files` 这类受保护目录，或所在盘是只读的
- 换到普通目录（如 `D:\Tools\NPlayerServer\`）即可

---

## 七、安全说明

- **只读**：PUT / DELETE / MKCOL / STOR / DELE 等写操作一律返回 405 / 502，无法通过本服务修改磁盘
- **密码明文**：配置文件里 `password` 是明文，这是刻意为之（方便忘记时查看）。程序保存时用 `icacls` 把文件 ACL 收紧到仅当前账户可读写
- **路径穿越防护**：虚拟路径解析后做前缀校验，`../../Windows` 这类请求返回 404
- **匿名风险**：密码留空 = 同一局域网内任何人无需登录即可读取共享目录。家里网络可用；**咖啡厅、公司、宿舍等公共网络务必设密码**
- 不要把 `config.json` 传到网盘或发给别人

---

## 八、从源码构建

需要 .NET 8 SDK。

```bat
cd NPlayerServer
dotnet build -c Release

:: 单文件发布（依赖目标机已装 .NET 8 运行时）
dotnet publish -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -o publish
```

如果要一个**不依赖运行时**的版本（体积约 70 MB）：

```bat
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -o publish-standalone
```

图标由 `tools\make_icon.py` 生成，纯标准库实现 PNG 编码与 ICO 封装，不需要装 Pillow：

```bat
python tools\make_icon.py
```

---

## 九、已知限制

- 仅 Windows；配置文件与 exe 同目录，放在受保护目录会保存失败（有提示）
- FTP 只有被动模式（PASV/EPSV），主动模式（PORT）不支持——现代客户端默认都是被动模式，不受影响
- `autoStart` 字段存在但界面未接线，开机自启需自行把快捷方式放进 `shell:startup`
- 托盘图标依赖 exe 内嵌图标，用单文件发布时通过 `Environment.ProcessPath` 提取
