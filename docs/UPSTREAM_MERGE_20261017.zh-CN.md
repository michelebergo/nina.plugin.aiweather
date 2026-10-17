# 2026-10-17 上游 1.15.8 融合记录

## 范围与状态

ADVANCED 基线为 `0a03ed9`（1.35.0.0）；本次完整融合上游 `70111eb..a59bbe1`，共三个提交：

1. `7fd3330`：按相机视角和站点暗度组合提示词；无法辨认的天空返回 Unknown（#17）。
2. `8962efd`：更新 Claude 模型发现、默认值及手动模型 ID 的保存（#18）。
3. `a59bbe1`：上游 1.15.8.0 发布元数据和说明。

本轮只进行源码融合、Release 配置编译和离线测试。不安装插件、不启动或重启 NINA、不推送远端、不创建或修改 Release。保留 ADVANCED 仓库身份及 1.35.0.0 程序集/清单版本，不把上游版本号当作 ADVANCED 发布版本；下一次实际发布时再统一递增。

## 合并取舍

| 上游内容 | ADVANCED 适配 |
| --- | --- |
| 全天鱼眼 / 固定视角、光污染 / 暗夜站点提示词 | 保留上游规则，新增中英文字段；默认仍为全天鱼眼和有光污染站点。修订后的噪声/不可读规则对所有组合生效，不能承诺与旧版本结果完全一致。 |
| 明确的 Unknown 结果 | 单独标记 `IsSkyUnreadable`，区分“成功判断天空不可读”和“请求失败而没有结果”。前者立即报告 Unsafe；后者保持既有失败和过期处理。 |
| Claude 模型更新 | 接纳新版 `claude-*` 系列，排除旧文本模型；采用上游 Haiku 5.5 默认值和新建议列表。保留现有非空用户模型 ID，不在刷新列表时强行替换。 |
| 可编辑模型输入框 | 改为双向 Text 绑定，离开输入框时保存手动 ID。Gemini 免费仍使用专门的排序池，不受此控件改变。 |
| 上游测试 | 合并并适配 ADVANCED 的来源/错误元数据；实时测试保持 Explicit，不在离线回归中调用云 API。 |
| 上游版本与打包 | 不覆盖 ADVANCED 的版本、身份、打包裁剪和发布流程。本轮未增加模型文件或依赖包。 |

保留本分支的单终端共享视频会话、ONNX 学生回退、在线调用比例、主节点优先接管、安全新鲜度保护、取消传播和教师—学生数据集。普通 Gemini 仍是严格单模型；Gemini 免费仍逐模型隔离配额，并保留 3.8 Flash 在 3.7 Flash 前及已移除 2.x 的默认顺序。界面备用模型列表改为直接复用同一个免费池默认定义，避免两份列表漂移。

## 不可读天空的安全与训练语义

只有提供商成功解析出明确的 `condition: "Unknown"`，才认定为有效的“天空不可读”观测。拼错类别、数字枚举、非法 JSON、HTTP 错误、超时和失败回退不能借此刷新安全结果。

- 强制 `isSafe=false`，置信度最高 30；云量 100 是保守协议占位值，不是真实云量测量。
- 正常在线结果可立即撤销已有 Safe，主节点快照原因明确为 `sky-unreadable`。
- Gemini 免费池遇到有效 Unknown 即结束本次轮询，不继续换模型寻找晴天结论，也不让学生结果覆盖它。
- Unknown 不参与相同帧结果复用；后续检查仍有机会重新识别。
- 不能作为有效教师标签，也不计算教师—学生云量差。启用隔离记录时仅写入 `quarantine`，`teacher.valid=false`，不写入可训练的 `labels` 目录。
- API 失败仍不能凭无效结果刷新天气分析时间；现有安全过期及有效本地回退政策保持不变。

## 主从配置与隐私

将 `CameraView`、`SiteDarkness` 和已有的 `SiteNotes` 一并纳入加密接管配置及配置修订。每次分析创建独立的异步提示词作用域，使用当前有效配置的快照：从节点接管时不会误用其本机可编辑默认值，也不会把同步内容写回本机设置。并发检查之间相互隔离。

从节点只读摘要显示视角、暗度和“备注已配置/未配置”，不显示备注原文。备注会作为提示词发送给所选分析服务，因此不要在备注中填写密码或 API 密钥。提示词版本升级为 `weather-json-v1.2-20261017-view{0|1}-site{0|1}`，用于追踪标签解释规则；该版本标识不包含备注原文。

兼容性约束：新字段的默认值不参与 JSON 序列化，保留旧默认配置的规范化 JSON/HMAC。新客户端读取旧配置时使用默认值；启用非默认视角、暗度或非空备注后，旧客户端因不认识这些字段而无法验证配置修订，将拒绝该接管配置。**先升级主从两端，再启用新配置**；不要假定旧端能安全接管。这个兼容规则针对接管配置，不代表普通状态同步一定中断。

## 验证记录

- `dotnet test NINA.Plugin.AIWeather.Test/NINA.Plugin.AIWeather.Test.csproj -c Release --filter 'TestCategory!=Live' -p:PostBuildEvent= --no-restore`：123 项通过，0 失败。
- `dotnet run --project tests/AIWeather.DatasetSmoke/AIWeather.DatasetSmoke.csproj -c Release -p:PostBuildEvent= --no-restore --no-launch-profile`：完整 smoke 套件通过，含既有数据集及集群回归。
- 新增覆盖：有效 Unknown 停止免费池轮询并撤销 Safe、失败 Unknown 不刷新结果、无效类别拒绝、隔离区实际落盘、异步提示词作用域和并发隔离、加密配置往返、字段修订变化、默认序列化兼容、只读摘要不泄露备注。
- `git diff --check`：通过；编译无错误，项目已有兼容性警告不在本轮扩展处理。
- `PostBuildEvent` 被禁用，测试构建不会向 NINA 插件目录复制 DLL。

测试使用模拟 HTTP、临时测试数据及回环接口；没有调用实际 AI API、连接摄像头或驱动屋顶/赤道仪。没有实测新提示词的现场识别质量，也没有替代用户在从节点上的现场验收。新版规则可能改变在线标签分布，正式上线后宜先核对标签检查器中的 Unknown 与暗夜站点样本。

参考：[上游 #17 提交](https://github.com/michelebergo/nina.plugin.aiweather/commit/7fd3330)、[上游 #18 提交](https://github.com/michelebergo/nina.plugin.aiweather/commit/8962efd)、[Claude 模型文档](https://platform.claude.com/docs/en/models/overview)、[Haiku 5.5 迁移说明](https://platform.claude.com/docs/en/models/haiku-5-5/migration-guide)。
