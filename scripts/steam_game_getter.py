from pathlib import Path
import platform
import sys
import vdf

def find_steam_dir() -> Path | None:
    system = platform.system()

    if system == "Windows":
        try:
            import winreg
            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Software\Valve\Steam",
            ) as key:
                return Path(winreg.QueryValueEx(key, "SteamPath")[0])
        except:
            return "C:\\Program Files (x86)\\Steam"

    candidates = {
        "Linux": [
            Path.home() / ".steam/steam",
            Path.home() / ".local/share/Steam",
        ],
        "Darwin": [
            Path.home() / "Library/Application Support/Steam",
        ],
    }

    for path in candidates.get(system, []):
        if path.exists():
            return path

    return None


def get_steam_game_dir(app_id, steam_dir: Path | None = None) -> Path | None:
    app_id = str(app_id)
    steam_dir = steam_dir or find_steam_dir()
    if steam_dir is None:
        return None

    library_file = steam_dir / "steamapps" / "libraryfolders.vdf"

    with library_file.open(encoding="utf-8") as f:
        libraries = vdf.load(f)

    for library in libraries["libraryfolders"].values():
        if not isinstance(library, dict):
            continue

        if str(app_id) not in library.get("apps", {}):
            continue

        library_path = Path(library["path"])

        manifest_path = (
            library_path
            / "steamapps"
            / f"appmanifest_{app_id}.acf"
        )

        with manifest_path.open(encoding="utf-8") as f:
            manifest = vdf.load(f)

        install_dir = manifest["AppState"]["installdir"]

        return (
            library_path
            / "steamapps"
            / "common"
            / install_dir
        )

    return None


# Example Usage 
if __name__ == "__main__":
    # Example: Half-Life 2 (App ID 220)
    app_id = 220
    if len(sys.argv) > 1:
        app_id = int(sys.argv[1])
    path = get_steam_game_dir(app_id)
    
    # py steam-game-getter.py 4584420
    # py steam-game-getter.py 4576510
    
    if path:
        print(f"Game \"{app_id}\" found at: {path}")
    else:
        print(f"Game \"{app_id}\" not installed or Steam not found.")   


