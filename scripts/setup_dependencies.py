import shutil
import os
import sys
from pathlib import Path

def find_and_copy_tool(tool_name, dest_dir):
    print(f"Searching for {tool_name}...")
    path = shutil.which(tool_name)
    
    if not path:
        # Try common paths if not in PATH
        if tool_name == "mkvmerge":
            common = [
                r"C:\Program Files\MKVToolNix\mkvmerge.exe",
                r"C:\Program Files (x86)\MKVToolNix\mkvmerge.exe"
            ]
            for p in common:
                if os.path.exists(p):
                    path = p
                    break
    
    if path:
        print(f"Found {tool_name} at: {path}")
        try:
            shutil.copy2(path, dest_dir / f"{tool_name}.exe")
            print(f"Copied to {dest_dir}")
            
            # For mkvmerge/eac3to/ffmpeg, check for neighboring DLLs?
            # heuristic: copy all .dll from source dir
            source_dir = Path(path).parent
            for file in source_dir.glob("*.dll"):
                shutil.copy2(file, dest_dir)
            
            print("Copied associated DLLs if any.")
            return True
        except Exception as e:
            print(f"Error copying {tool_name}: {e}")
            return False
    else:
        print(f"❌ {tool_name} not found in PATH or standard locations.")
        return False

def main():
    root = Path(__file__).parent.parent
    bin_dir = root / "bin"
    bin_dir.mkdir(exist_ok=True)
    
    tools = ["ffmpeg", "ffprobe", "eac3to", "mkvmerge"]
    
    success_count = 0
    for tool in tools:
        if find_and_copy_tool(tool, bin_dir):
            success_count += 1
            
    print(f"\nDone. Copied {success_count}/{len(tools)} tools to {bin_dir}")

if __name__ == "__main__":
    main()
