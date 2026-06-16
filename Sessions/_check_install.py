import ast
from pathlib import Path

module = ast.parse(Path("Sessions/billing230-install.py").read_text(encoding="utf-8"))
files = None
for node in module.body:
    if isinstance(node, ast.Assign):
        for t in node.targets:
            if isinstance(t, ast.Name) and t.id == "FILES":
                files = ast.literal_eval(node.value)
if files is None:
    raise SystemExit("FILES not found")
adb = files["Casazen.Infrastructure/Data/AppDbContext.cs"]
for token in ["ConsentRecord", "RentSchedule", "StripeCustomerId", "PlatformInvoice"]:
    print(token, token in adb)
