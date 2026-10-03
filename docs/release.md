# 1. 改版本号（唯一来源，改这一处就够）
#    Directory.Build.props  <Version>0.2.1</Version>

$axhub_ver = '0.2.1'

git commit -am "Version $axhub_ver"

# 2. 打 tag（必须 v + 版本号，与上面逐字一致）
git tag v$axhub_ver

# 3. 推送（commit 和 tag 都要到远端）
git push --follow-tags
