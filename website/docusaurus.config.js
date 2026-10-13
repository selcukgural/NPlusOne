// @ts-check
// The documentation site. Its pages are the repository's own docs/ folder, so GitHub and the site show the same text;
// docs/backlog.md is the maintainers' work log and stays out.

import {themes as prismThemes} from 'prism-react-renderer';

const repository = 'https://github.com/selcukgural/NPlusOne';
// The three packages and nothing else (the owner's other packages and look-alike names stay out).
const nuget = 'https://www.nuget.org/packages?q=owner%3Aselcukgural+NPlusOne';

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'N+1',
  tagline: 'Find N+1 queries in .NET and EF Core code, at build time and in tests',
  favicon: 'img/icon.png',

  future: {
    v4: true,
  },

  url: 'https://selcukgural.github.io',
  baseUrl: '/NPlusOne/',
  organizationName: 'selcukgural',
  projectName: 'NPlusOne',
  trailingSlash: false,

  onBrokenLinks: 'throw',

  markdown: {
    // The docs are plain Markdown written for GitHub too: no MDX, so `List<Order>` outside code is just text.
    format: 'detect',
    hooks: {
      onBrokenMarkdownLinks: 'throw',
    },
  },

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          path: '../docs',
          routeBasePath: 'docs',
          sidebarPath: './sidebars.js',
          exclude: ['backlog.md'],
          editUrl: `${repository}/edit/main/docs/`,
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      }),
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      colorMode: {
        respectPrefersColorScheme: true,
      },
      navbar: {
        title: 'N+1',
        logo: {
          alt: 'N+1',
          src: 'img/logo.svg',
        },
        items: [
          {type: 'docSidebar', sidebarId: 'guide', position: 'left', label: 'Guide'},
          {to: '/docs/rules/NP0001', label: 'Rules', position: 'left'},
          {to: '/docs/comparison', label: 'Comparison', position: 'left'},
          {
            type: 'dropdown',
            label: 'Packages',
            position: 'right',
            items: [
              {label: 'Which package where', to: '/docs/guide/packages'},
              {label: 'NPlusOne.Analyzers', href: 'https://www.nuget.org/packages/NPlusOne.Analyzers'},
              {label: 'NPlusOne.EntityFrameworkCore', href: 'https://www.nuget.org/packages/NPlusOne.EntityFrameworkCore'},
              {label: 'NPlusOne.AspNetCore', href: 'https://www.nuget.org/packages/NPlusOne.AspNetCore'},
              {label: 'All on NuGet', href: nuget},
            ],
          },
          {href: repository, label: 'GitHub', position: 'right'},
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: 'Docs',
            items: [
              {label: 'Getting started', to: '/docs/guide/getting-started'},
              {label: 'Rules', to: '/docs/rules/NP0001'},
              {label: 'Runtime guard', to: '/docs/runtime'},
            ],
          },
          {
            title: 'Packages',
            items: [
              {label: 'Which package where', to: '/docs/guide/packages'},
              {label: 'NPlusOne.Analyzers', href: 'https://www.nuget.org/packages/NPlusOne.Analyzers'},
              {label: 'NPlusOne.EntityFrameworkCore', href: 'https://www.nuget.org/packages/NPlusOne.EntityFrameworkCore'},
              {label: 'NPlusOne.AspNetCore', href: 'https://www.nuget.org/packages/NPlusOne.AspNetCore'},
              {label: 'Changelog', href: `${repository}/blob/main/CHANGELOG.md`},
            ],
          },
          {
            title: 'Project',
            items: [
              {label: 'GitHub', href: repository},
              {label: 'Issues', href: `${repository}/issues`},
              {label: 'Contributing', href: `${repository}/blob/main/CONTRIBUTING.md`},
            ],
          },
        ],
        copyright: `N+1 is free and open source under the MIT license.`,
      },
      prism: {
        theme: prismThemes.github,
        darkTheme: prismThemes.dracula,
        additionalLanguages: ['csharp', 'ini', 'bash', 'json'],
      },
    }),
};

export default config;
