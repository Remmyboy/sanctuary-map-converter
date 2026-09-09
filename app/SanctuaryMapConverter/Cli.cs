using SanctuaryMapConverter.Core;

namespace SanctuaryMapConverter
{
    // Headless mode: the same orchestration the window drives, callable from
    // a terminal, for scripting a batch of conversions.
    //
    //   SanctuaryMapConverter --convert <sourceFolder> [--cc0] [--biome B] [--name X]
    //       [--out <mapsRoot>] [--deploy] [--prop-ext .santp|.sanprop] [--no-props] [--no-border]
    //   SanctuaryMapConverter --validate <path.sanmap> [--check-textures]
    //       [--gamedata <dir>] [--lua] [--managed <dir>]
    //   SanctuaryMapConverter --check-deployed
    //   SanctuaryMapConverter --tool <name> [...]
    internal static class Cli
    {
        public static int Run(string[] args) => args[0] switch
        {
            "--convert" => Convert(args),
            "--validate" => Validate(args),
            "--check-deployed" => CheckDeployedCmd(args),
            "--tool" => Tools.ToolsCli.Run(args),
            _ => Usage(),
        };

        static int Usage()
        {
            Console.Error.WriteLine(
                "usage: --convert <sourceFolder> [--cc0] [--biome B] [--name X] [--out dir] [--deploy] [--no-props] [--no-border]\n" +
                "       --validate <map.sanmap> [--check-textures] [--lua]\n" +
                "       --check-deployed\n" +
                "       --tool <name> [...]");
            return 2;
        }

        /// Check every converted map already sitting in the game.
        ///
        /// This used to also build the named maps and mirror them into the
        /// editor tree. Conversion deploys to both trees as it goes, so what
        /// is left worth doing is the sweep: parse each of our deployed maps
        /// with the game's own parsers and resolve every asset it names.
        static int CheckDeployedCmd(string[] args)
        {
            string sanctuary = GamePaths.FindSanctuaryInstall();
            if (sanctuary == null)
            {
                Console.Error.WriteLine("no Sanctuary install found");
                return 2;
            }
            return DeployedCheck.Run(
                GamePaths.EngineMaps(sanctuary),
                GamePaths.EditorMaps(sanctuary),
                GamePaths.GamedataDir(sanctuary),
                GamePaths.ManagedDir(sanctuary),
                "~TEAM-1v1_Tropical_256_47940",
                Console.WriteLine) ? 0 : 1;
        }

        static int Convert(string[] args)
        {
            var o = new ConvertOptions();
            bool deploy = false;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--cc0": o.Cc0Textures = true; break;
                    case "--biome": o.Biome = args[++i]; break;
                    case "--name": o.Name = args[++i]; break;
                    case "--out": o.OutputMapsRoot = args[++i]; break;
                    case "--deploy": deploy = true; break;
                    case "--prop-ext": o.PropExtension = args[++i]; break;
                    case "--no-props": o.NoProps = true; break;
                    case "--no-border": o.Border = false; break;
                    default:
                        if (o.Source == null) o.Source = args[i];
                        else { Console.Error.WriteLine($"unknown argument: {args[i]}"); return 2; }
                        break;
                }
            }
            if (o.Source == null) return Usage();

            string fa = GamePaths.FindFaInstall();
            string sanctuary = GamePaths.FindSanctuaryInstall();
            o.ScdPath = GamePaths.ScdPath(fa);
            (o.PackDir, o.TableCsv) = GamePaths.DataFiles(GamePaths.FindDataDir());
            o.OutputMapsRoot ??= sanctuary != null
                ? GamePaths.EngineMaps(sanctuary)
                : Path.Combine(Environment.CurrentDirectory, "converted");

            if (!o.Cc0Textures && o.ScdPath == null)
            {
                Console.Error.WriteLine("source-texture mode needs a Forged Alliance install (env.scd); use --cc0 or install FA");
                return 2;
            }
            if (o.Cc0Textures && !GamePaths.HaveCc0Data(o.PackDir, o.TableCsv))
            {
                Console.Error.WriteLine(
                    "CC0 mode needs a 'data' folder beside the exe holding texture-map.csv and texturepack\\ " +
                    "- download texturepack.zip from the release, or build it with --tool build-texturepack");
                return 2;
            }

            try
            {
                var result = new Converter(o, Console.WriteLine).Run();
                if (deploy && sanctuary != null) Deployer.Deploy(result.MapDir, sanctuary, Console.WriteLine);
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAIL " + e.Message);
                return 1;
            }
        }

        static int Validate(string[] args)
        {
            var o = new ValidateOptions();
            var paths = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--check-textures": o.CheckTextures = true; break;
                    case "--gamedata": o.GamedataDir = args[++i]; break;
                    case "--lua": o.LuaCheck = true; break;
                    case "--managed": o.Managed = args[++i]; break;
                    default: paths.Add(args[i]); break;
                }
            }
            if (paths.Count == 0) return Usage();

            string sanctuary = GamePaths.FindSanctuaryInstall();
            o.Managed ??= sanctuary != null
                ? GamePaths.ManagedDir(sanctuary)
                : null;
            o.GameRoot ??= sanctuary;
            if (o.Managed == null)
            {
                Console.Error.WriteLine("validation needs a Sanctuary install (EM.Map.dll); pass --managed <dir>");
                return 2;
            }

            int failures = 0;
            foreach (var p in paths)
                if (!Validator.Check(p, o, Console.WriteLine)) failures++;
            return failures == 0 ? 0 : 1;
        }
    }
}
