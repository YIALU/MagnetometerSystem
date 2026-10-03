# 提交与审查流程

项目采用“功能分支 → GitHub PR 审查与 Windows CI → 合并 `master` → 手动同步 Gitee”。GitHub 必须收到提交后才能审查，因此审查发生在合并主分支前；本地 `git commit` 仍可正常执行。

远程名称固定区分：`github` 指向 `YIALU/MagnetometerSystem` 的 GitHub 仓库，`origin` 指向 Gitee。操作时明确写出远程，先用 `git remote -v` 核对地址。

## 一次性设置

仓库中的文件提供 CI、模板与审查指引；GitHub 合并规则及 Codex 账户设置需要另行在服务端启用，不能仅凭文件存在就认为已生效。

1. 在 Codex 网页连接 GitHub 仓库，在设置的 Review code 中为本仓库启用 Automatic review；同时核对 Personal preferences 的 Automatic review 和 Review trigger。选项以实际账户页面为准，选择能够覆盖新提交的触发方式。若新推送没有触发审查，在 PR 评论中手动输入 `@codex review`。[Codex 官方说明](https://learn.chatgpt.com/docs/third-party/github)
2. 在 GitHub 仓库 `Settings → Rules → Rulesets` 为 `master` 配置并启用规则：必须通过 PR 合并、必须通过 `Windows build and tests`、要求分支与主分支保持最新、要求解决审查讨论、禁止强制推送和删除主分支。必需检查应选 GitHub Actions 来源；若列表尚未显示该检查，先让 PR 工作流运行一次。
3. 单人维护时 required approvals 设为 **0**，不启用“必须由最后推送者以外的人批准”或 Code Owners 批准；有第二位审查者后再调整。允许 merge commit，首次历史同步不要启用线性历史要求；避免配置日常可绕过规则的主体。规则能力以仓库套餐和权限为准。[GitHub 规则说明](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets)

**Codex 审查评论不等于 GitHub 的 required approval，也不等于必需状态检查。** 这套配置通过 GitHub 强制 PR 和 Windows CI；维护者仍需确认最新提交已经获得 Codex 审查、处理严重问题后才合并。未运行、超时或额度不足都不能视为审查通过。本流程不引入需要 OpenAI API key 的付费 Action。

## 日常开发

### 1. 从已审查的主分支开始

先确保工作目录干净，并确认当前 HEAD 与最新 `github/master` 完全一致，再创建功能分支。若当前工作区有未提交工作，或本地主分支领先/分叉，请先妥善保留并单独处理，也可以使用干净的独立 worktree；不要用 `reset --hard` 或清理命令覆盖已有工作。

下面示例中的 `codex/my-change` 应替换为本次任务名，逐步检查命令结果：

```powershell
git status --short
git remote -v
git fetch github
# 当前 HEAD 应与 github/master 相同；不同则先处理已有差异。
git rev-parse HEAD
git rev-parse github/master
git switch -c codex/my-change github/master
```

### 2. 验证并推送功能分支

在 Windows、.NET 8 SDK 下运行：

```powershell
dotnet restore MagnetometerSystem.sln
dotnet build MagnetometerSystem.sln -c Debug --no-restore
dotnet test MagnetometerSystem.sln -c Debug --no-build
```

按改动风险补充 UI、协议、保存或真实设备验收，具体约束见 [AGENTS.md](AGENTS.md)。CI 会在 PR 创建和更新时运行 Windows 构建与测试；CI 成功不代表真实串口设备已验证。

只暂存本次涉及的具体文件，检查暂存差异后提交；以下路径是占位示例：

```powershell
git diff
git add -- path/to/changed-file
git diff --cached
git commit -m "描述本次变更"
git push -u github codex/my-change
```

不要使用 `git add .` 打包无关工作或个人配置，不直接推送 `master`。

### 3. 在 GitHub 审查并合并

1. 创建目标分支为 `master` 的 PR，填写问题、变化和验证证据。草稿完成后转为 Ready for review。
2. 确认 Codex 审查已触发；必要时评论 `@codex review`，等待实际审查结果。按反馈修复，并推送到同一功能分支。
3. **每次新的 push、解决冲突或更新基础分支之后，都要重新检查最新 PR HEAD 的 CI 和 Codex 审查。** 旧提交上的审查结果不覆盖新提交；自动审查没有运行时再次手动触发。
4. 最新 `Windows build and tests` 成功、最新提交审查完成且问题已处理后，由维护者合并。不要开启自动合并来跳过人工确认。未解决问题应说明处理结果，不能只勾选模板作为审查证据。

## 首次同步已有提交

GitHub 落后于本地/Gitee 时，先把缺失的**已提交历史**放到独立同步分支，通过 GitHub PR 审查后合并；不要把工作区尚未提交的业务代码或个人配置混入。

这个历史同步 PR 必须选择 **Create a merge commit**，保留原有提交作为祖先。不要 squash 或 rebase 这些已在 Gitee 存在的提交，否则提交身份变化会破坏随后向 Gitee 的快进同步。历史同步尚未完成时，先不要把 GitHub `master` 推到 Gitee。

## 手动同步 Gitee

仅在目标 PR 已完成审查并合入 GitHub `master` 后执行。下面脚本获取两个远程的最新状态，验证 Gitee 是 GitHub 主分支的祖先，再进行普通快进推送；不移动当前本地分支，也不推送标签。

```powershell
git fetch github
if ($LASTEXITCODE -ne 0) { throw '获取 GitHub 失败，停止同步。' }
git fetch origin
if ($LASTEXITCODE -ne 0) { throw '获取 Gitee 失败，停止同步。' }
git merge-base --is-ancestor origin/master github/master
if ($LASTEXITCODE -ne 0) { throw 'Gitee 无法快进到 GitHub master，请先检查历史差异。' }
git log --oneline origin/master..github/master
# 核对上方提交均来自已审查并合并的 GitHub PR，再执行：
git push origin refs/remotes/github/master:refs/heads/master
```

若推送被拒绝，重新获取并检查差异；禁止使用 `--force` 或 `--force-with-lease` 覆盖 Gitee 主分支。不要用双远程自动推送代替上述审查与同步步骤。
