---
name: magnetometer-github-review
description: "仅用于 YIALU/MagnetometerSystem 磁力仪项目的开发收尾、提交推送、创建 GitHub PR、处理 Codex 审查、合并、同步 Gitee 与发布两平台 Release。执行功能分支、Windows 验证、最新提交审查、检查通过后直接合并 GitHub、同步 Gitee，并更新两平台 Release 软件包；不用于其他仓库或纯代码解释。"
---

# 磁力仪项目 GitHub 审查流程

流程：功能分支 → Windows 验证 → GitHub PR → 处理审查反馈 → 核对最新提交的 CI 与审查 → 检查通过后直接合并 GitHub → 同步 Gitee 主分支 → 发布两平台 Release 软件包。审查发生在合并主分支前；本地 commit 不会触发 GitHub 审查。

## 项目与任务范围

- 项目为 `YIALU/MagnetometerSystem`，从 `git rev-parse --show-toplevel` 定位根目录，不依赖某台机器的绝对路径。下文文件路径均相对该根目录。
- 远程名称：`origin` 指向 `github.com/YIALU/MagnetometerSystem`（GitHub，审查与合并的主平台）；`gitee` 指向 `gitee.com/yialu/MagnetometerSystem`（Gitee 镜像与更新源）。先用 `git remote -v` 验证 URL（HTTPS/SSH 均可）；名称与地址不符时先查清目的地，不能按惯例猜测或擅自重配远程。
- 主分支为 `master`；新任务分支使用 `codex/任务名`（沿用仓库已有约定）。推送明确写出远程和分支，不使用裸 `git push`。
- 读取根目录 `AGENTS.md`、`README.md`、`CONTRIBUTING.md` 和涉及模块。若当前旧分支没有贡献指南，读取经 fetch 更新的 `origin/master:CONTRIBUTING.md`，或 [GitHub 主分支指南](https://github.com/YIALU/MagnetometerSystem/blob/master/CONTRIBUTING.md)。不要用旧分支缺文件作为跳过流程的理由。
- 以当前用户请求和已有授权确定终点：只读审查、仅本地修改不自动扩展成发布；用户明确要求按此流程提交 PR 时，执行提交、推送任务分支、创建 PR 和处理反馈，不反复询问已授权步骤。用户已授权本项目在最新提交的 CI 和 Codex 审查通过后直接合并 GitHub，再核验并同步 Gitee 主分支，每次软件代码交付同时更新 GitHub 和 Gitee Release 软件包，无需重复询问合并、同步或发布权限。用户明确要求只审查、仅本地修改、只提交不合并、仅操作 GitHub、暂不同步或不发布时遵从该要求。不改仓库保护、账户设置或增加付费服务。

## Claude Code 环境

- **GitHub CLI**：用已登录的 `gh` 创建 PR、读取检查与评论、合并和发布。`gh` 不在 PATH 时（刚安装、终端未刷新）使用完整路径，Windows 默认为 `C:\Program Files\GitHub CLI\gh.exe`。未登录时请用户自己运行 `gh auth login`；不读取、打印或转写令牌，不把凭据写进命令行、文件、日志或 PR。
- **PR 绑定**：`gh pr create` 后调用桌面 App 的 PR 状态工具（`get_status`）；未绑定时用 `bind_pr` 绑定 PR 链接。App 只接受会话所在 checkout 的 `origin` 仓库，因此会话目录应是 PR 分支所在的工作树，且 `origin` 为 GitHub。
- **等待 CI 与审查**：绑定后由 App 跟踪 CI 并通知；不自行用定时唤醒、循环或 `gh pr checks --watch` 轮询。需要判断时读取一次当前状态（PR head SHA、`Windows build and tests`、Codex 审查摘要），未完成就报告并等待通知或用户继续。
- **工作树**：并行任务使用独立 worktree；会话需要处理某个 PR 时，把会话目录移到该工作树，而不是在其他窗口使用的目录切换分支。

## 准备分支，保留并行工作

先检查工作区、暂存区、未跟踪文件和两个远程的历史关系。区分本任务修改、其他窗口的工作与尚未同步的已提交历史。

- 并行任务优先复用合适的独立 worktree，否则创建独立 worktree；不要在其他窗口使用的目录切换分支、合并或重置。不同 worktree 也应使用各自的构建输出，避免共享目录中并行构建/测试造成 DLL 占用。
- 新开发以最新 `origin/master` 为基线；已有开发分支先保留工作，再将最新主分支安全合入。创建 worktree 不会自动复制未提交修改；只转入核实属于本任务的文件或补丁，并检查未跟踪的新源码是否齐全。归属不明且影响提交范围时才澄清。
- 本地/Gitee 领先 GitHub 的旧提交不自动混进普通功能 PR。确需同步历史时，按贡献指南单独准备历史同步 PR，保留原提交身份，并在交付说明中要求 **Create a merge commit**，不用 squash/rebase 改写已发布历史。
- 确认任务分支包含 `.github/workflows/windows-ci.yml` 和流程文档。未提交的 `AGENTS.md` 若与主分支同名新增文件冲突，应保留本地产品约束并整合新增审查规则，不能覆盖任何一方。
- 只暂存本任务的明确路径并检查 `git diff --cached`；已有暂存内容也要核对。不要用 `git add .` 混入其他任务、个人配置、数据库或日志，不用 `reset --hard`、强推或清理命令丢弃工作。
- 仓库中部分文件为 CRLF/LF 混合行尾，且本机可能启用 `core.autocrlf=true`。编辑这类文件后用 `git diff --cached --numstat` 与加 `--ignore-cr-at-eol` 的结果对比；出现纯行尾改动时恢复原行尾，并按原样字节暂存（`git hash-object -w --no-filters` + `git update-index --cacheinfo`），不要提交整文件行尾翻转。

## 软件交付前核对版本

**每次改动**（代码、文档、配置、skill 均包括在内）在提交 PR 前读取 `Directory.Build.props`（唯一版本源）和两平台现有标签、Release，并在同一 PR 中变更版本号。版本必须为尚未正式发布的新版本；用户指定版本优先，否则当前版本已发布时默认递增补丁版本，并更新 `docs/变更日志.md`。版本修改必须进入同一 PR 的 CI 与审查，不能合并后才补改版本。同一 PR 内的后续推送沿用本 PR 已递增的版本；每个新 PR 都在主分支当前版本的基础上再递增，即使主分支的版本尚未正式发布，也不与其他 PR 共用版本号。选定版本前核实两平台均无同名标签或正式发布（Gitee 标签可匿名查询 `https://gitee.com/api/v5/repos/yialu/MagnetometerSystem/tags`）。已发布版本或标签不得移动、覆盖或通过重命名旧软件包伪造新版本。

每次改动都要提交 PR 审查，审查通过后合并 GitHub 并同步 Gitee。只读审查不产生改动；纯文档 / skill 维护同样升版本号、走 PR、合并并同步，但不生成软件发布包；软件代码改动按下述完整交付流程同时更新两平台 Release。

## Windows 验证与 GitHub PR

代码变更在 Windows/.NET 8 环境执行仓库要求的验证，按改动风险补充业务链路、UI 或串口检查：

```powershell
dotnet restore MagnetometerSystem.sln
dotnet build MagnetometerSystem.sln -c Debug --no-restore
dotnet test MagnetometerSystem.sln -c Debug --no-build --no-restore -m:1
```

逐条检查退出码；上一步失败时先处理，不继续用旧构建产物声称测试通过。仅文档/skill 改动做适当格式与内容验证，不添加复述文本的业务测试；GitHub 必需 CI 仍应正常运行。测试数量取自本次结果，分别报告构建、单元/业务链路、UI、真实设备验证；未做串口验证或环境跳过应如实标明。产品不变量与风险用例以 `AGENTS.md` 和存在时的 `docs/testing-and-acceptance.md` 为准。有互连串口对时，设置 `MAGNETOMETER_TEST_RX_PORT`（软件端）和 `MAGNETOMETER_TEST_TX_PORT`（模拟设备端）运行 `OptionalSerialLoopbackTests` 与 `OptionalSerialChainTests`；虚拟串口结果不能写成真实设备验证。

已获提交 PR 授权时：

1. 在任务分支提交本次改动，显式推送到 `origin` 的同名分支（`git push -u origin codex/任务名`），禁止直推 `master`。若工具结果不明，先检查远端分支和 PR，避免重复创建或重复提交。
2. 复用本任务已有开放 PR；否则向 `YIALU/MagnetometerSystem:master` 创建 PR，按 `.github/PULL_REQUEST_TEMPLATE.md` 写明问题、变化和实际验证证据。只有确实准备好审查才转为非草稿；用户明确要求草稿时保留草稿，并说明尚未进入自动审查阶段。创建后按上文绑定 PR。
3. 使用已认证的 `gh` 读取状态，必要时使用浏览器；不把凭据写进文件、日志或 PR。

## 每次推送后完成审查

仓库配置预期为所有 PR、每次 push 自动审查，并要求 `Windows build and tests`。这是需要核验的预期，不能仅凭文件或历史记录宣称已生效。

- 读取 PR 当前 head SHA，核对该提交的 Windows 检查和 Codex review summary/审查提交。旧检查、旧评论、单独一个无提交关联的 👍 不代表最新提交已通过。
- 确认审查已触发；若未触发且没有同一提交正在运行的审查，在已授权 PR 审查的范围内评论一次 `@codex review`。发送后若结果不明先核对评论，避免重复触发。
- 处理有效反馈；有证据认为不适用时解释理由。修复后验证并推送到同一分支，重查新的 head SHA。合并基础分支或解决冲突也会产生新提交，需要重跑检查与审查。
- 讨论线程在问题得到处理并说明证据后再解决，不能仅为解除合并阻塞而标记完成。
- Codex 评论/👍 不等于 GitHub required approval 或必需状态检查。交付前确认最新提交的必需 CI 成功、Codex 完成且有效问题已处理；读取当前实际分支规则（`gh api repos/YIALU/MagnetometerSystem/rules/branches/master`），不擅自绕过规则。
- 等待方式见“Claude Code 环境”。明确权限/额度/服务故障、审查未运行或超时应报告为未完成，附上 PR、提交和阻塞原因；不要用反复评论/重推触发重试，也不要把没有结果解释为无问题。缺少可用远程工具时完成本地工作并交代缺失环节。

## 检查通过后合并 GitHub

用户已授权将审核通过后的直接合并作为本项目默认流程，不再单独请求合并确认；用户明确限制本次任务不合并时除外。这是用户对交付终点的约定，旧流程文档中“交给用户确认合并”的文字不再作为额外确认要求；实际仓库保护规则仍必须满足。

- 最新 PR head SHA 的 `Windows build and tests` 及其他必需检查成功；Codex 审查摘要对应同一提交并确认完成，有效问题已处理、相关讨论已解决。旧提交结果、仅有 👍、审查运行中或额度不足均不能当作通过。
- 合并前重新读取 head SHA 和分支规则；head 变化时先核验新提交的 CI 与审查。合并请求绑定已核验的 head SHA（`gh pr merge <PR> --merge --match-head-commit <SHA>`），默认使用 merge commit 保留历史，不能用管理员绕过检查或开启自动合并让 PR 在 Codex 审查完成前被合入。
- 核实 GitHub 返回 merged 状态和合并提交，再 fetch `origin/master` 确认合并已存在；随后执行 Gitee 同步。结果不明时先读取远端状态，不盲目重复合并。CI、审查、规则或权限未满足时保持 PR 开放，报告缺失环节。

交付包含 PR 链接、分支和提交、CI 与审查证据、合并结果、Gitee 同步结果及未验证范围。**软件代码改动**还要包含两平台 Release 链接与软件包校验结果，不能把“已提交 PR”或“代码已同步”当作完整软件交付终点；**纯文档 / 配置 / skill 改动**在合并与 Gitee 同步完成后即结束，不打标签、不创建 Release，版本号保持未发布。

## GitHub 合并后同步 Gitee

本项目默认流程包含 GitHub 合并后的 Gitee 代码同步。确认本任务 PR 已合入 `origin/master` 后继续执行，不再单独询问同步权限；用户明确要求仅操作 GitHub、暂缓或跳过 Gitee 时除外。合并 GitHub 前必须满足上面的 CI 与审查门槛，不能绕过审查或分支规则。

先从 GitHub 远端核实 PR 的 merged 状态和合并提交；重新 fetch 两个远程，检查 GitHub 主分支待同步的全部提交均为已审查内容。验证 `gitee/master` 是 `origin/master` 的祖先；若历史分叉，停止同步并说明差异，不能强推或用 squash/rebase 掩盖分叉。网络、认证或权限失败时保留现有分支并报告未完成，不将尝试推送当作同步成功。`gitee` 的 SSH 地址不可用（无密钥或 22 端口不通）时，可对同一仓库的 HTTPS 地址执行相同的精确 refspec 推送，凭据由 Git 凭据管理器提供；不读取或打印凭据。

使用明确的 `gitee` 和精确 refspec，只快进同步主分支：

```powershell
git fetch origin
if ($LASTEXITCODE -ne 0) { throw '获取 GitHub 失败，停止同步。' }
git fetch gitee
if ($LASTEXITCODE -ne 0) { throw '获取 Gitee 失败，停止同步。' }
git merge-base --is-ancestor gitee/master origin/master
if ($LASTEXITCODE -ne 0) { throw 'Gitee 无法快进，停止同步并检查历史差异。' }
git log --oneline gitee/master..origin/master
# 推送前核对上方全部提交均已通过对应 PR 的最新 CI 与审查。
git push gitee refs/remotes/origin/master:refs/heads/master
if ($LASTEXITCODE -ne 0) { throw 'Gitee 推送失败，停止并核实远端状态。' }
git fetch gitee
if ($LASTEXITCODE -ne 0) { throw 'Gitee 推送后核验失败，不能报告同步成功。' }
$githubHead = git rev-parse origin/master
$giteeHead = git rev-parse gitee/master
if ($githubHead -ne $giteeHead) { throw '两平台 master 不一致，需要检查。' }
```

完成后核实 GitHub / Gitee 主分支的共同提交 SHA 和当前版本。软件代码改动再执行下方的标签与 Release 软件包发布；纯文档 / 配置 / skill 改动到此结束，不打标签、不发布。不配置双远程自动推送。

## 每次软件代码交付更新两平台 Release

用户已授权每次软件代码提交推送的交付包含 Release 软件包更新。任务分支的中间推送先完成最新提交的 CI 与审查；通过后直接合并 GitHub、同步 Gitee，再发布对应版本，无需额外请求发布确认。旧发布文档中单独询问合并、同步或发布权限的要求以此用户授权为准；版本、构建、校验和仓库保护要求仍应执行。用户明确限制本次任务不发布时除外。

1. 读取 `docs/发布流程.md` 和 `build.ps1`，在干净的独立 worktree 中准备已合并、已同步的确切源码提交。核实发布源码与已审查内容的对应关系；若合并引入未经验证的实质变化，先验证并处理。版本以 `Directory.Build.props` 为准，带说明的 `vX.Y.Z` 标签绑定该确切提交且两平台一致。已有标签指向其他提交或版本已正式发布时停止覆盖，先通过正常 PR 修正版本。
2. 在 Windows 运行 `.\build.ps1 -Mode All`，检查退出码、实际输出和版本；正式发布不得使用 `AllowDirty`、`SkipVersionCheck` 绕过检查。安装版需要带 `Languages\ChineseSimplified.isl` 的 Inno Setup 7。构建一次，保留 `artifacts/v<version>/` 中同一套安装版 `MagnetometerSystem-v<version>-setup.exe`、便携版 `MagnetometerSystem-v<version>-portable-win-x64.zip` 和 `SHA256SUMS.txt`。核对程序版本、标签、源码提交及清单哈希，不能上传旧构建或只改文件名。
3. 仅向明确的 `origin` 和 `gitee` 推送本次标签（`git push origin refs/tags/vX.Y.Z`、`git push gitee refs/tags/vX.Y.Z`，不使用 `--tags` 批量推送），随后在 GitHub（`gh release create ... --verify-tag`）和 Gitee 创建对应正式 Release，上传上一步相同的三个文件。Gitee Release 需要已登录的网页会话；未登录时请用户在浏览器自行登录，不代为输入账号密码。两平台版本、标签和源码一致，软件包字节一致。说明包含实际变化、对应 PR/源码提交及验证边界，用户摘要放在 `<!-- user-notes:start -->` 与 `<!-- user-notes:end -->` 之间；下载入口应指向实际发布的附件，不虚构链接。
4. 分别重新读取两平台 Release 元数据，核对版本、标签和附件完整性，并下载两平台附件验证 SHA256 与本地清单一致。核实双平台更新检测需要的版本和附件信息正确；仅本地构建或上传请求成功不能替代发布后的校验。
5. 恢复中断任务前先查看现有标签、Release 和附件，复用同一版本尚未完成的发布，避免重复创建。认证、网络、构建或上传失败时保留有效结果，报告具体平台与缺失附件，并继续可完成的步骤；不能把“代码已推送”声称为软件包已更新，也不能静默遗漏一个平台。已正式发布的有效旧版本不替换，新的源码使用新版本。

最终交付必须给出两平台 Release 链接、版本、源码提交和附件校验结果；软件包尚未发布或未验证时明确标注未完成。
