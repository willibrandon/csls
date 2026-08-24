import { defineConfig } from "astro/config";
import sitemap from "@astrojs/sitemap";
import starlight from "@astrojs/starlight";

export default defineConfig({
  site: "https://willibrandon.github.io",
  base: "/csls",
  trailingSlash: "always",
  integrations: [
    starlight({
      title: "csls",
      description: "C# language intelligence for editors, terminals, and agents.",
      credits: false,
      components: {
        MarkdownContent: "./src/components/MarkdownContent.astro",
      },
      social: [
        {
          icon: "github",
          label: "GitHub",
          href: "https://github.com/willibrandon/csls",
        },
      ],
      sidebar: [
        { slug: "", label: "Overview" },
        { slug: "getting-started" },
        { slug: "editors" },
        { slug: "language-server" },
        { slug: "configuration" },
        { slug: "cli" },
        { slug: "dashboard" },
        { slug: "mcp" },
        { slug: "architecture" },
        { slug: "rpc-and-control", label: "RPC and control" },
        { slug: "dependencies" },
        { slug: "testing" },
        { slug: "performance" },
        { slug: "migration" },
        { slug: "releases" },
        { slug: "troubleshooting" },
        { slug: "development" },
      ],
    }),
    sitemap(),
  ],
});
