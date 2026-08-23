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
        { slug: "cli" },
        { slug: "mcp" },
        { slug: "development" },
      ],
    }),
    sitemap(),
  ],
});
