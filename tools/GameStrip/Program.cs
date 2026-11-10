// Makes a stripped copy of an Oxygen Not Included install for headless test runs (CI), and
// measures what is in one. docs/DEVELOPMENT.md, "The stripped game", says how CI uses it.
//
//   census GAMEDIR              the streamed payloads of every data file and bundle, by file,
//                               class and fate (kept or stripped), and the largest files
//   strip GAMEDIR OUT           writes the stripped copy to OUT (an empty or missing folder)
//
// GAMEDIR is a Linux install (OxygenNotIncluded_Data/ beside the executable), symbolic links
// followed, with any DLC content laid into StreamingAssets as the game's own installs have it.
// The copy keeps every serialized file and every header; it drops the texel, mesh, audio and
// video payloads the simulation never reads (Selection.cs says which are kept and why), the
// FMOD banks' sample data (Banks.cs), and the files Moono never loads (Unity's player and
// Mono). A player reads it with ONI_NO_CONTENT=1 (the engine's no-content mode).
using GameStrip;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: GameStrip census GAMEDIR | GameStrip strip GAMEDIR OUT");
    return 2;
}
var game = Path.GetFullPath(args[1]);
var dataDir = Path.Combine(game, "OxygenNotIncluded_Data");
if (!Directory.Exists(dataDir))
{
    Console.Error.WriteLine($"{game}: no OxygenNotIncluded_Data folder");
    return 2;
}
switch (args[0])
{
    case "census":
        Census.Run(game);
        return 0;
    case "strip" when args.Length == 3:
        return Strip.Run(game, Path.GetFullPath(args[2]));
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}
