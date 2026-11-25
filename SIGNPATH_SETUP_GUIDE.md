# Free Public Code Signing & Blog Distribution Guide

Ei guide follow kore apni **100% FREE** publicly trusted code signing paben (SignPath Foundation + Free Microsoft Store) ebong nijer blog-e **Google AdSense** chaliye safe download share korte parben.

---

## Step 1: Public GitHub Repository-te Upload Korun

1. [github.com](https://github.com)-e notun ekta **Public** repository create korun (e.g., `WinFixCleanPortable`).
2. Ei project folder (`e:\Anti Gravity\Software`) theke terminal open kore push korun:

```powershell
git init
git add .
git commit -m "Initial open-source release v1.1.0"
git branch -M main
git remote add origin https://github.com/<YOUR_USERNAME>/WinFixCleanPortable.git
git push -u origin main
```

---

## Step 2: Prothom Public Release Toiri Korun (Auto via GitHub Actions)

SignPath apply korar jonno GitHub-e at least 1-ta public release thaka lage:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

- Tag push korlei `.github/workflows/release.yml` automatically `.NET 9` diye `WinFixCleanPortable.exe` build korbe, `SHA256SUMS.txt` banabe, ebong GitHub **Releases** tab-e publish kore dibe!

---

## Step 3: Free Public Code Signing-er Jonno SignPath Foundation-e Apply Korun

1. **[signpath.org](https://signpath.org)** -> Open Source Code Signing page-e jan.
2. Application form-e din:
   - **Repository URL:** `https://github.com/<YOUR_USERNAME>/WinFixCleanPortable`
   - **License:** MIT License (`LICENSE` file already included)
   - **Download Page:** Apnar GitHub Releases URL ba apnar Blog Download Page URL
   - **Description:** Clear open-source Windows maintenance utility (Windows Repair, Large/ZIP/ISO Finder, Duplicate Cleaner, Smart Fast Copy — zero ads/PUPs/telemetry inside the binary).
3. Approve hole SignPath dashboard theke **GitHub.com Trusted Build System** connect korun ebong GitHub Repository -> **Settings -> Secrets and variables -> Actions**-e ei 4-ta secret add korun:
   - `SIGNPATH_API_TOKEN`
   - `SIGNPATH_ORGANIZATION_ID`
   - `SIGNPATH_PROJECT_SLUG` (e.g., `winfix-clean-portable`)
   - `SIGNPATH_POLICY_SLUG` (e.g., `release-signing`)
4. Tarpor notun tag push korun (e.g., `git tag v1.1.1 && git push origin v1.1.1`) -> GitHub Actions automatically **SignPath-signed `WinFixCleanPortable.exe`** publish kore dibe!

---

## Step 4: Free Microsoft Store Account (Optional — Apnar Nijer Publisher Name!)

1. **[storedeveloper.microsoft.com](https://storedeveloper.microsoft.com)**-e jan -> **Individual developer (free)** select korun (`$0`, no credit card).
2. Government ID (NID / Passport) + selfie diye verify korun.
3. Microsoft Store-e app submit korle Microsoft nijei sign kore host kore — shob Windows 10/11 PC-te **ZERO SmartScreen warning** ebong apnar nijer verified developer name dekhabe!

---

## Step 5: Blog + Google AdSense Best Practices

- **SEO Post Structure:** Alada alada guide post likhun (e.g., *"How to Find & Delete Large ISO/ZIP Files on Windows 11"*, *"How to Fix Missing Windows System Files Without Reinstalling"*, *"Fast Multi-Threaded Copy Without Duplicates"*).
- **Trust Signals:** Download button-er niche `SHA-256` hash (from `SHA256SUMS.txt`), **VirusTotal scan link**, ebong **Open-Source GitHub link** din.
- **Defender False-Positive Removal (Free):** Proti release-er pore [Microsoft Security Intelligence](https://www.microsoft.com/en-us/wdsi/filesubmission)-e **"Software developer"** option diye `.exe` submit korun (free).
