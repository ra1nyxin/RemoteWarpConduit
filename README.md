# RemoteWarpConduit

Experimental Oxygen Not Included Spaced Out mod for transferring ordinary elements between separate games through a Python relay. This is a development build and requires in-game validation.

The current protocol supports gas, liquid, and ordinary solid elements with mass, temperature, and disease. Food, seeds, equipment, and other stateful objects are not supported. Select a destination in the sender's action menu; the original local mode remains available.

Supply teleporter inputs and outputs still need a duplicant to activate them, but do not require the Field Research skill while this mod is enabled.

The relay uses Python 3 and SQLite from the standard library. Run `python relay.py`; its default TCP port is 53573 and its database is created next to the script. The client connects to the configured IP and port. There is no authentication or encryption.

Run `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` to compile and install the mod to `%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\Dev\RemoteWarpConduit`. The project expects the game assemblies at the `OniManagedDir` path in the project file.
