# ProjectHub 项目协作与交付平台

ProjectHub 是面向本地部署场景的项目工时与项目资产管理系统。系统以项目为核心，不引入客户、审批、请假、成本预算等无关模块，重点覆盖员工工时填报、项目成员角色、交付资产、版本历史、目录权限、通知提醒和自定义报表。

## 技术栈

- 后端：.NET 10、ASP.NET Core Web API、SqlSugar、PostgreSQL。
- 前端：Vue 3、TypeScript、vue-pure-admin、Element Plus、Vite。
- 认证：JWT 访问令牌与刷新令牌双 Token，登录验证码。
- API 文档：Swagger/OpenAPI，包含控制器、接口和模型 XML 注释。
- 部署：ASP.NET Core 统一托管前端静态文件，支持 Docker Compose。

## 核心功能

- 项目团队双维度管理：负责人、管理员、参与者、只读等项目权限，以及结构、硬件、固件、软件、测试等可多选专业岗位和职责说明。
- 员工周工时填报、批量保存、复制、计时器、周确认与管理员锁定。
- 工时统计、自定义报表模板、预览及 Excel/CSV 导出。
- 项目资产工种分类、任意层级目录、拖拽批量上传、版本历史和回收站。
- 目录成员权限：不可访问、只读、可编辑，支持父目录继承。
- 图片、PDF、Markdown、代码、表格、音视频和部分三维模型在线预览。
- 当前资产一键 ZIP 打包，可选择包含全部历史版本。
- 双层物理存储：可见工作区与隐藏不可变版本库分离。
- 站内通知、SMTP 邮件、失败重试、日工时和周确认自动提醒。
- 用户账号、工时规则、操作日志、存储一致性检查与重建。

## 本地开发快速启动

前置环境：.NET SDK 10、Node.js 22.22 或更高版本、pnpm 11、PostgreSQL。

开发数据库默认配置：

```text
数据库：projecthub
用户：postgres
密码：123456
端口：5432
```

启动后端：

```powershell
dotnet run --project .\src\ProjectHub.Api\ProjectHub.Api.csproj --launch-profile http
```

启动前端开发服务器：

```powershell
Set-Location .\src\ProjectHub.Web
pnpm install
pnpm dev
```

访问地址：

- 前端开发服务器：<http://localhost:8848>
- 后端统一托管页面：<http://localhost:5157>
- Swagger：<http://localhost:5157/swagger>
- 健康检查：<http://localhost:5157/health>

开发环境初始管理员默认为 `admin / 123456`。首次登录后应立即修改密码；生产环境禁止继续使用默认密码和开发 JWT 密钥。

## 验证与发布

```powershell
dotnet test .\ProjectHub.slnx -c Release
Set-Location .\src\ProjectHub.Web
pnpm typecheck
pnpm build
```

前端生产构建默认输出到 `src/ProjectHub.Api/wwwroot`，并在同一目录生成供在线版本检测使用的 `version.json`。也可以在仓库根目录执行：

```powershell
.\scripts\publish.ps1
```

发布结果默认位于 `artifacts/publish`。

## 文档索引

- [用户使用手册](docs/用户使用手册.md)
- [开发文档](docs/开发文档.md)
- [部署与运维手册](docs/部署与运维手册.md)
- [资产存储与权限设计](docs/资产存储与权限设计.md)
- [API 与认证说明](docs/API与认证说明.md)
