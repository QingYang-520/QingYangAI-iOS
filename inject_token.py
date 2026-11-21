# -*- coding: utf-8 -*-
"""
CI 打包前把登录 API 令牌注入 BuildConstants.cs（明文不进仓库）。
用法：python3 inject_token.py <token>
"""
import re
import sys

tok = sys.argv[1] if len(sys.argv) > 1 else ""
if not tok:
    print("token 为空，跳过注入")
    sys.exit(0)

p = "BuildConstants.cs"
s = open(p, encoding="utf-8").read()
s2, n = re.subn(
    r'(LoginApiToken\s*=\s*")[^"]*(")',
    lambda m: m.group(1) + tok + m.group(2),
    s, count=1,
)
if n == 0:
    print("未找到 LoginApiToken 常量，注入失败", file=sys.stderr)
    sys.exit(1)
open(p, "w", encoding="utf-8").write(s2)
print("token injected, length =", len(tok))
