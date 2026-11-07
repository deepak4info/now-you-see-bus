#!/usr/bin/env python3
import os
import sys
import time
import io
import ftplib

server = os.environ.get("SMARTERASP_FTP_SERVER") or os.environ.get("FTP_SERVER") or "win8080.site4now.net"
username = os.environ.get("SMARTERASP_FTP_USER") or os.environ.get("SERVER_USERNAME") or "fatehabad4info-001"
password = (
    os.environ.get("SMARTERASP_FTP_PASSWORD")
    or os.environ.get("SERVER_PASSWORD")
    or os.environ.get("SMARTERASP_PASSWORD")
    or ""
).strip()
target_dir = os.environ.get("SMARTERASP_FTP_DIR") or os.environ.get("FTP_DIR") or ""
local_dir = sys.argv[1] if len(sys.argv) > 1 else "./publish"

if not password:
    print("❌ ERROR: No deployment password found in GitHub Secrets!")
    print("👉 Please add 'SERVER_PASSWORD' or 'SMARTERASP_FTP_PASSWORD' in your GitHub Repository Secrets:")
    print("   GitHub -> Settings -> Secrets and variables -> Actions -> Repository secrets")
    print("   NOTE: As indicated on your SmarterASP Server Details, this password is [Same as Control Panel]!")
    sys.exit(1)

print(f"📡 Connecting to SmarterASP FTP server '{server}:21' as user '{username}'...")
try:
    ftp = ftplib.FTP(server, timeout=45)
    ftp.login(username, password)
    ftp.set_pasv(True)
    print("✅ FTP Login successful!")
except ftplib.error_perm as e:
    print(f"❌ FTP Authentication Failed (530): {e}")
    print("👉 IMPORTANT: Your SmarterASP FTP password is the EXACT SAME password you use to log into your SmarterASP Control Panel.")
    print("   Please update 'SERVER_PASSWORD' or 'SMARTERASP_FTP_PASSWORD' in GitHub Secrets with your SmarterASP Control Panel login password.")
    sys.exit(1)
except Exception as e:
    print(f"❌ Failed to connect to FTP server: {e}")
    sys.exit(1)

pwd = ftp.pwd()
print(f"📂 Current FTP directory: {pwd}")
try:
    root_contents = ftp.nlst()
    print(f"📋 Directory listing: {root_contents}")
except Exception as e:
    print(f"⚠️ Could not list root: {e}")
    root_contents = []

# If user specified a directory, try it
if target_dir and target_dir != "/":
    try:
        ftp.cwd(target_dir)
        print(f"📂 Changed directory to target: {target_dir}")
    except Exception as e:
        print(f"ℹ️ Could not cd to '{target_dir}' ({e}). Checking relative directory...")
        clean_dir = target_dir.strip("/")
        try:
            ftp.cwd(clean_dir)
            print(f"📂 Changed directory to: {clean_dir}")
        except Exception:
            pass

# Check if 'site1' subfolder exists and we're not already in it
curr_files = ftp.nlst()
if "site1" in curr_files and "web.config" not in curr_files:
    print("📂 Entering 'site1' directory...")
    ftp.cwd("site1")
    curr_files = ftp.nlst()

print(f"🎯 Final deployment directory: {ftp.pwd()}")
print(f"📋 Current files in directory: {len(curr_files)} items found")

# Step 1: Upload app_offline.htm to unlock IIS DLL files
print("\n⏸️ [1/3] Uploading 'app_offline.htm' to release IIS file locks on BusTrack.Api.dll...")
app_offline_content = b"""<!DOCTYPE html>
<html>
<head><title>Site Updating</title></head>
<body style="font-family:sans-serif;text-align:center;padding:50px;">
  <h2>Deploying WhereIsMyBus API update...</h2>
  <p>Please wait a few seconds while new binaries are loaded.</p>
</body>
</html>"""
try:
    ftp.storbinary("STOR app_offline.htm", io.BytesIO(app_offline_content))
    print("✅ 'app_offline.htm' active. Waiting 4 seconds for IIS worker process to shut down and release DLL locks...")
    time.sleep(4)
except Exception as e:
    print(f"⚠️ Warning uploading app_offline.htm: {e}")

# Step 2: Recursively upload all publish files
print(f"\n🚀 [2/3] Uploading publish files from '{local_dir}'...")

uploaded_count = 0

def upload_folder(src_folder):
    global uploaded_count
    items = sorted(os.listdir(src_folder))
    for item in items:
        local_path = os.path.join(src_folder, item)
        if os.path.isdir(local_path):
            try:
                ftp.mkd(item)
            except Exception:
                pass # Already exists
            ftp.cwd(item)
            upload_folder(local_path)
            ftp.cwd("..")
        else:
            if item in ("app_offline.htm", "app_offline.htm.deploy"):
                continue
            with open(local_path, "rb") as f:
                ftp.storbinary(f"STOR {item}", f)
            uploaded_count += 1
            if uploaded_count % 10 == 0 or uploaded_count < 5:
                print(f"  ⬆️ Uploaded {uploaded_count} files (latest: {item})")

try:
    upload_folder(local_dir)
    print(f"✅ Successfully uploaded {uploaded_count} files!")
except Exception as e:
    print(f"❌ Error during file upload: {e}")
    # Always attempt to remove app_offline.htm even on failure so the site isn't stuck offline
    try:
        ftp.delete("app_offline.htm")
    except Exception:
        pass
    sys.exit(1)

# Step 3: Remove app_offline.htm to bring the site back online
print("\n▶️ [3/3] Removing 'app_offline.htm' to start IIS with new code...")
try:
    ftp.delete("app_offline.htm")
    print("✅ 'app_offline.htm' removed! IIS worker process is starting up.")
except Exception as e:
    print(f"⚠️ Warning deleting app_offline.htm: {e}")

ftp.quit()
print("\n🎉 AUTOMATED BACKEND DEPLOYMENT COMPLETED SUCCESSFULLY!")
