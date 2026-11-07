# ProjectHub API 与认证说明

## 1. Swagger

应用启动后访问：

```text
http://localhost:5157/swagger
```

Swagger 页面包含接口中文注释、请求模型、响应模型和 Bearer 认证按钮。先调用登录接口取得访问令牌，再点击页面右上角“Authorize”输入令牌。

## 2. 统一响应

普通 JSON 接口返回：

```json
{
  "success": true,
  "code": "OK",
  "message": "操作成功",
  "data": {}
}
```

业务失败由异常中间件转换为稳定错误码和对应 HTTP 状态码。文件下载、ZIP 和健康检查不使用普通 JSON 包装。

## 3. 双 Token 流程

1. `GET /api/auth/captcha` 获取一次性验证码。
2. `POST /api/auth/login` 提交用户名、密码、验证码 ID 和验证码。
3. 响应返回 `accessToken`、`refreshToken` 和过期时间。
4. 业务请求使用：

```http
Authorization: Bearer {accessToken}
```

5. 访问令牌过期后调用 `POST /api/auth/refresh`。
6. 刷新成功会轮换刷新令牌，旧刷新令牌立即失效。
7. `POST /api/auth/logout` 撤销当前刷新令牌。

刷新令牌不能用于调用业务 API，数据库只保存刷新令牌哈希。

## 4. 主要路由

| 路由前缀 | 功能 |
| --- | --- |
| `/api/auth` | 验证码、登录、刷新、登出和当前用户 |
| `/api/projects` | 项目、成员权限、专业岗位、职责和项目任务 |
| `/api/time-entries` | 工时、批量保存、周确认和计时器 |
| `/api/reports` | 报表模板、预览和导出 |
| `/api/projects/{projectId}/assets` | 资产分类、文件、版本、ZIP 和存储检查 |
| `/api/projects/{projectId}/assets/folders` | 目录树、目录移动和目录 ACL |
| `/api/notifications` | 通知分页、未读状态和工时提醒 |
| `/api/users` | 用户账号和用户选项 |
| `/api/system` | 工时规则和操作日志 |
| `/api/dashboard` | 首页统计 |

### 4.1 项目成员契约

项目成员采用“项目权限 + 专业岗位 + 职责”结构。`projectRole` 决定成员能否管理项目、填报工时或只读访问；`jobRoles` 是可多选的专业岗位数组，不参与权限计算：

```json
{
  "userId": 12,
  "projectRole": 1,
  "jobRoles": [64, 128, 16384],
  "customJobTitle": null,
  "responsibility": "负责硬件原理图、PCB、固件联调和样机验证"
}
```

每位成员至少需要一个专业岗位。选择“其他岗位”时必须填写 `customJobTitle`。每个项目必须且只能有一名项目负责人。后端将岗位数组转换为 `project_members.jobrolemask` 位掩码持久化，对外接口始终返回数组，调用方不需要执行位运算。

## 5. 文件上传与下载

资产上传使用 `multipart/form-data`：

- 首次上传：`POST /api/projects/{projectId}/assets`
- 新版本：`POST /api/projects/{projectId}/assets/{assetId}/versions`

下载历史版本：

```text
GET /api/projects/{projectId}/assets/{assetId}/versions/{versionId}/download
```

下载支持 HTTP Range。ZIP 打包：

```text
GET /api/projects/{projectId}/assets/package
```

可用查询参数：`categoryId`、`folderId`、`includeDescendants`、`includeAllVersions`。

## 6. 存储管理接口

仅项目管理角色可调用：

```text
GET  /api/projects/{projectId}/assets/storage/status?deepVerify=false
POST /api/projects/{projectId}/assets/storage/rebuild
```

深度检查和重建可能读取大量文件，因此支持请求取消并建议由管理页面触发。

## 7. 安全约束

- 任何项目资源接口都不信任前端传入的角色信息。
- 项目 ID、目录 ID、资产 ID 和版本 ID 会在后端重新校验所属关系。
- 文件路径只保存相对路径，解析后必须仍位于配置的资产根目录。
- 文件扩展名使用白名单，文件名通过 `Path.GetFileName` 和安全字符处理。
- 目录权限会应用到查询、详情、下载、ZIP 和通知，防止信息侧漏。
- 生产 JWT 密钥、数据库密码和 SMTP 密码必须通过环境变量或安全配置中心注入。
