from pathlib import Path
import platform
import sys
import shutil
import os
import requests
import zipfile
import subprocess

from steam_game_getter import get_steam_game_dir

BEPINEX_LINK = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"

def download_and_unzip(url: str, destination_dir: str) -> None:
    destination = Path(destination_dir)
    destination.mkdir(parents=True, exist_ok=True)

    response = requests.get(url, stream=True, timeout=60)
    response.raise_for_status()

    zip_path = destination / "TEMP_DOWNLOAD.zip"

    with open(zip_path, "wb") as f:
        for chunk in response.iter_content(chunk_size=8192):
            if chunk:
                f.write(chunk)

    with zipfile.ZipFile(zip_path, "r") as zip_ref:
        zip_ref.extractall(destination)

    zip_path.unlink()  # Remove ZIP after extraction

    print(f"Successfully extracted files to: {destination}")

def find_solution_root():
    filename = "CasualtiesTogether.sln"

    # Search from the current working directory, up to 2 levels up
    cwd = Path.cwd()
    cwd_dirs = [cwd, cwd.parent, cwd.parent.parent]

    # Search from the script's real location, up to 2 levels up
    script_dir = Path(__file__).resolve().parent
    script_dirs = [
        script_dir,
        script_dir.parent,
        script_dir.parent.parent,
    ]

    # Search both locations, avoiding duplicate directories
    searched = set()

    for directory in cwd_dirs + script_dirs:
        directory = directory.resolve()

        if directory in searched:
            continue
        searched.add(directory)

        candidate = directory / filename

        if candidate.is_file():
            return candidate

    raise FileNotFoundError(
        f"Could not find {filename!r}!"
    )

    
def remove_line_with_prefix(filepath, prefix):
    filepath = Path(filepath)
    if not filepath.exists():
        return
    lines = filepath.read_text().splitlines(keepends=True)

    for i, line in enumerate(lines):
        if line.startswith(prefix):
            del lines[i]
            break

    filepath.write_text("".join(lines))




def main():
    sln_file = find_solution_root()
    project_root = sln_file.parent
    os.chdir(project_root)

    
    aaaa = input("Game type (Demo/Fullgame)").lower().strip()

    folder = "CU_Demo"
    appid = 4576510
    if aaaa == "fullgame":
        appid = 4584420
        folder = "CU_Fullgame"
    
        
    path = get_steam_game_dir(appid)
    
    

    links = [
        ### BepInEx
        BEPINEX_LINK,

        ### BepInEx.Debug 
        #"https://github.com/BepInEx/BepInEx.Debug/releases/download/r11.1/ConstructorProfiler_r11.1.zip",
        #"https://github.com/BepInEx/BepInEx.Debug/releases/download/r11.1/CtorShotgun_r11.1.zip",
        #"https://github.com/BepInEx/BepInEx.Debug/releases/download/r11.1/DemystifyExceptions_r11.1.zip",
        #"https://github.com/BepInEx/BepInEx.Debug/releases/download/r11.1/MirrorInternalLogs_r11.1.zip",
        "https://github.com/BepInEx/BepInEx.Debug/releases/download/r11.1/SimpleMonoProfiler_r11.1.zip",
        #"https://github.com/BepInEx/BepInEx.Debug/releases/download/r11.1/StartupProfiler_r11.1.zip",
    ]

    output_dir = Path(f"{folder}")

    if not output_dir.exists():
        print("Running dotnet restore...")
        subprocess.run(["dotnet", "restore"], check=True)

    output_dir.mkdir(parents=True, exist_ok=True)
    
    if path:
        print("Copying game...")
        shutil.copytree(path, str(output_dir), dirs_exist_ok=True)
        
        game_preinstalled_bepinex_path_check = output_dir / "BepInEx"
        if game_preinstalled_bepinex_path_check.exists():
            shutil.rmdir(str(game_preinstalled_bepinex_path_check))

        print("Patching game...")
        remove_line_with_prefix(output_dir / "CasualtiesUnknown_Data" / "boot.config", "single-instance=")
        
        print("Downloading BepInEx...")
        download_and_unzip(BEPINEX_LINK,output_dir)

        print("Done!")
    else:
        print(f"Game \"{appid}\" not installed or Steam not found.")


    pass




if __name__ == "__main__":

    main()


