# 防火墙规则回归

```powershell
dotnet run --project tools/FirewallRegression/FirewallRegression.csproj -c Release
```

6 项检查覆盖：路径独立标识、端口及 helper 参数限制、只读查询不写规则、异常响应处理、隔离假 COM 存储中的实际 PowerShell 新建/重复开启/换端口/查询/重复移除，以及 Windows 防火墙的真实只读查询。含中文、单引号与 PowerShell 表达式形态的路径作为纯数据测试。

测试不请求 UAC，不修改电脑的真实防火墙规则。提权写入和 UAC 取消的最终验收需用户在「设置 → 网络」操作；状态检测与隔离存储测试不能当作真实提权写入验收。

规则属性参考：[INetFwRules.Add](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nf-netfw-inetfwrules-add)、[LocalSubnet 来源范围](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nf-netfw-inetfwrule-put_remoteaddresses)、[防火墙策略状态](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nn-netfw-inetfwpolicy2)。
