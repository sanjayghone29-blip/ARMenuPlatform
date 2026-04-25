# .mind File Generation SOP

Manual process for generating MindAR target files (`.mind`) for each QR code in the ARMenuPlatform pilot.

**When to follow this:** Every time a new QR code is generated for a table (any restaurant). Until server-side auto-generation is built (planned at ~50 restaurants).

**Time per QR:** ~2 minutes.

---

## 1. Download the QR PNG from Azure Blob

1. Go to [Azure Portal](https://portal.azure.com) → Storage Account → **Containers** → `qr-codes`
2. Open the restaurant folder (e.g. `qr-codes/2/` for restaurant ID 2)
3. Find the QR PNG you just generated. Filename pattern: `{timestamp}_Table-{N}.png`
   - Example: `1745923812_Table-3.png`
4. Click the file → **Download**
5. Note the exact filename (you'll need it in step 4)

---

## 2. Compile the .mind File

1. Open https://hiukim.github.io/mind-ar-js-doc/tools/compile
2. Click **"Click to upload your image (Drop or Click)"** in the top-left panel
3. Select the QR PNG you downloaded
4. Wait for compilation. Progress bar shows on the right. Takes 10–30 seconds.
5. When done, click **"Download"** button (top-right). A file called `targets.mind` downloads.

---

## 3. Rename the .mind File

The filename **must exactly match the QR PNG stem**, because the AR viewer code computes the .mind URL by replacing `.png` with `.mind` on the QR image URL.

| QR PNG filename | Required .mind filename |
|---|---|
| `1745923812_Table-3.png` | `1745923812_Table-3.mind` |

Rename `targets.mind` → `{same stem}.mind`. Match case exactly.

---

## 4. Upload to Azure Blob

1. Azure Portal → Storage Account → **Containers** → `qr-codes`
2. Open the **same restaurant folder** as the PNG (e.g. `qr-codes/2/`)
3. Click **Upload** (top toolbar)
4. Select the renamed `.mind` file
5. **Important:** Leave "Overwrite if files already exist" unchecked unless you're regenerating
6. Click **Upload**

The PNG and .mind file should now sit side by side in the same folder.

---

## 5. Verify

Open this URL in a browser, replacing the values:

```
https://{storage-account}.blob.core.windows.net/qr-codes/{restaurantId}/{timestamp}_Table-{N}.mind
```

- ✅ File downloads → upload succeeded, AR will work for this table
- ❌ 404 / "BlobNotFound" → wrong filename, wrong folder, or upload failed

Then test on phone:
1. Scan the QR with phone camera
2. Tap a dish → tap "Start AR Camera"
3. Point camera at the printed QR
4. Video should overlay on the QR within 2–5 seconds

If camera opens but never detects → .mind file exists but doesn't match the QR (re-do steps 1–4 carefully).

---

## Common Errors & Fixes

### "AR Marker Not Configured" fallback screen appears
- .mind file is missing or unreachable at the expected URL
- Check filename exactly matches PNG stem (case-sensitive)
- Check it's in the correct restaurant folder
- Verify the URL directly in browser (step 5)

### Camera opens but never detects the QR
- .mind file exists but was compiled from a different image
- You may have uploaded one restaurant's .mind file under another restaurant's folder
- Re-download the correct PNG → recompile → re-upload

### "Click to upload" on compiler page doesn't accept the file
- File is corrupted or not a valid PNG
- Re-download from Azure Blob

### Compilation finishes but tracking is unreliable on phone
- Pure black-and-white QR codes have limited trackable features
- **Fix:** Print the QR with a colored border, restaurant logo above/below, or on a textured background. Recompile from a photo of the actual printed marker for best results.

### CORS error in browser console when AR loads
- Storage account CORS is already set to `*` for the pilot
- If you see CORS errors, verify in Azure Portal: Storage Account → **Resource sharing (CORS)** → `qr-codes` rule → Allowed origins includes `*` or your app domain

### Browser shows old .mind file after re-upload
- Hard refresh phone browser (Chrome: pull down to refresh, or close tab and reopen)
- Or append `?v=2` to the QR scan URL once to bust cache

### File size warning during upload
- Normal .mind file is 50KB–500KB
- If yours is multiple MB, the source PNG was too large; downscale to 512×512 or 1024×1024 and recompile

---

## Quick Checklist (print this)

- [ ] Downloaded PNG from `qr-codes/{restaurantId}/`
- [ ] Compiled at hiukim.github.io compiler tool
- [ ] Renamed `targets.mind` → `{exact-PNG-stem}.mind`
- [ ] Uploaded to **same folder** as PNG
- [ ] Verified URL returns the file in browser
- [ ] Tested AR on phone end-to-end