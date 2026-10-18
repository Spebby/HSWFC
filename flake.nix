{
  description = "Dev environment for Unity";
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
    git-hooks = {
      url = "github:cachix/git-hooks.nix";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    {
      self,
      nixpkgs,
      flake-utils,
      git-hooks,
      ...
    }:
    flake-utils.lib.eachDefaultSystem (
      system:
      let
        pkgs = import nixpkgs { inherit system; };
      in
      {
        checks = {
          pre-commit-check = git-hooks.lib.${system}.run {
            src = ./.;
            excludes = [
              ".*/submodules/.*"
              "^submodules/.*"
            ];
            hooks = {
              flake-checker.enable = true;
              nixfmt = {
                enable = true;
                settings.width = 100;
              };
              statix = {
                enable = true;
                settings.ignore = [ "flake.lock" ];
              };
              nil.enable = true;
              shellcheck.enable = true;
              shfmt.enable = true;
              typos.enable = true;
            };
          };
        };

        devShells.default = pkgs.mkShell {
          packages = with pkgs; [
            git
            gh

            # Dotnet env for rider
            dotnet-sdk_8
            omnisharp-roslyn
            mono
            msbuild
          ];

          shellHook = ''
            echo "Dev Env loaded."
            ${self.checks.${system}.pre-commit-check.shellHook}
          '';
        };
      }
    );
}
