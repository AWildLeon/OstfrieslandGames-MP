{
  description = "Online-Mehrspieler-Mod (BepInEx) und Server fuer Ostfriesland Games";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
      version = "0.1.0";

      bepinexVersion = "5.4.23.4";
      bepinexZip = pkgs: pkgs.fetchurl {
        url = "https://github.com/BepInEx/BepInEx/releases/download/v${bepinexVersion}/BepInEx_linux_x64_${bepinexVersion}.zip";
        hash = "sha256-JyLDfGklHh/o0lcHyOz0HZuMktgxHGgyqkd3yduawPY="; # bepinex
      };
    in
    {
      packages = forAllSystems (pkgs: rec {
        server = pkgs.buildDotnetModule {
          pname = "ogmultiplayer-server";
          inherit version;
          src = pkgs.lib.fileset.toSource {
            root = ./.;
            fileset = pkgs.lib.fileset.unions [
              ./src/Shared
              ./src/OGMultiplayer.Server
            ];
          };
          projectFile = "src/OGMultiplayer.Server/OGMultiplayer.Server.csproj";
          nugetDeps = ./nix/server-deps.json;
          dotnet-sdk = pkgs.dotnetCorePackages.sdk_8_0;
          dotnet-runtime = pkgs.dotnetCorePackages.runtime_8_0;
          executables = [ "OGMultiplayer.Server" ];
          meta = {
            description = "Lobby/Relay-Server fuer den Ostfriesland Games Online-Mehrspieler-Mod";
            license = pkgs.lib.licenses.mit;
            mainProgram = "OGMultiplayer.Server";
          };
        };
        default = server;
      } // pkgs.lib.optionalAttrs (pkgs.stdenv.hostPlatform.system == "x86_64-linux") {
        # Installs BepInEx + builds the plugin against the user's own game copy.
        install = pkgs.writeShellApplication {
          name = "og-multiplayer-install";
          runtimeInputs = [ pkgs.dotnetCorePackages.sdk_8_0 pkgs.unzip pkgs.coreutils pkgs.gnused pkgs.curl ];
          text = ''
            work="$(mktemp -d)"
            trap 'rm -rf "$work"' EXIT
            cp -r ${self}/src ${self}/scripts "$work/"
            chmod -R u+w "$work"
            export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
            export BEPINEX_ZIP=${bepinexZip pkgs}
            bash "$work/scripts/install.sh" "$@"
          '';
        };
        # Starts the game with BepInEx inside an FHS environment (NixOS).
        play = pkgs.writeShellApplication {
          name = "og-multiplayer-play";
          runtimeInputs = [ pkgs.steam-run-free ];
          text = ''
            if [ $# -lt 1 ]; then
              echo "Usage: og-multiplayer-play \"/pfad/zu/Ostfriesland Games\" [Spiel-Argumente...]" >&2
              exit 2
            fi
            cd "$1"
            shift
            exec steam-run ./run_bepinex.sh ./OG.x86_64 "$@"
          '';
        };
      });

      apps = forAllSystems (pkgs:
        let p = self.packages.${pkgs.stdenv.hostPlatform.system}; in
        {
          server = { type = "app"; program = "${p.server}/bin/OGMultiplayer.Server"; };
          default = self.apps.${pkgs.stdenv.hostPlatform.system}.server;
        } // pkgs.lib.optionalAttrs (p ? install) {
          install = { type = "app"; program = "${p.install}/bin/og-multiplayer-install"; };
          play = { type = "app"; program = "${p.play}/bin/og-multiplayer-play"; };
        });

      nixosModules.default = { config, lib, pkgs, ... }:
        let cfg = config.services.ogmultiplayer; in
        {
          options.services.ogmultiplayer = {
            enable = lib.mkEnableOption "Ostfriesland Games Online-Mehrspieler-Server";
            package = lib.mkOption {
              type = lib.types.package;
              default = self.packages.${pkgs.stdenv.hostPlatform.system}.server;
              description = "Server-Paket.";
            };
            port = lib.mkOption {
              type = lib.types.port;
              default = 27420;
              description = "TCP-Port.";
            };
            bind = lib.mkOption {
              type = lib.types.str;
              default = "0.0.0.0";
              description = "Bind-Adresse (\"::\" fuer IPv6 + IPv4).";
            };
            openFirewall = lib.mkOption {
              type = lib.types.bool;
              default = false;
              description = "TCP-Port in der Firewall oeffnen.";
            };
            passwordFile = lib.mkOption {
              type = lib.types.nullOr lib.types.str;
              default = null;
              example = "/run/secrets/ogmultiplayer-password";
              description = ''
                Datei mit dem Server-Passwort (erste Zeile). null = offener Server.
                Wird per systemd LoadCredential eingelesen und landet nicht im Nix-Store.
              '';
            };
            extraArgs = lib.mkOption {
              type = lib.types.listOf lib.types.str;
              default = [ ];
              example = [ "--ready-timeout" "90" ];
              description = "Weitere Argumente fuer den Server.";
            };
          };

          config = lib.mkIf cfg.enable {
            systemd.services.ogmultiplayer = {
              description = "Ostfriesland Games Online-Mehrspieler-Server";
              wantedBy = [ "multi-user.target" ];
              after = [ "network-online.target" ];
              wants = [ "network-online.target" ];
              serviceConfig = {
                ExecStart = lib.escapeShellArgs (
                  [ (lib.getExe cfg.package) "--port" (toString cfg.port) "--bind" cfg.bind
                    "--state-file" "/var/lib/ogmultiplayer/state.json" "--no-console" ]
                  ++ lib.optionals (cfg.passwordFile != null) [ "--password-file" "%d/password" ]
                  ++ cfg.extraArgs
                );
                LoadCredential = lib.mkIf (cfg.passwordFile != null) [ "password:${cfg.passwordFile}" ];
                DynamicUser = true;
                # bans and admin password changes (state.json)
                StateDirectory = "ogmultiplayer";
                StateDirectoryMode = "0700";
                Restart = "on-failure";
                NoNewPrivileges = true;
                ProtectSystem = "strict";
                ProtectHome = true;
                PrivateTmp = true;
                PrivateDevices = true;
                ProtectKernelTunables = true;
                ProtectKernelModules = true;
                ProtectControlGroups = true;
                RestrictAddressFamilies = [ "AF_INET" "AF_INET6" ];
                LockPersonality = true;
                MemoryDenyWriteExecute = false; # .NET JIT
              };
              environment.DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            };
            networking.firewall.allowedTCPPorts = lib.mkIf cfg.openFirewall [ cfg.port ];
          };
        };

      devShells = forAllSystems (pkgs: {
        default = pkgs.mkShell {
          packages = [ pkgs.dotnetCorePackages.sdk_8_0 pkgs.unzip pkgs.curl ]
            ++ pkgs.lib.optional (pkgs.stdenv.hostPlatform.system == "x86_64-linux") pkgs.steam-run-free;
          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
        };
      });

      formatter = forAllSystems (pkgs: pkgs.nixfmt);
    };
}
