// @ts-check
// The guide from A to Z: start, use the analyzer, use the guard, configure, run in CI, then the rules and the internals.

/** @type {import('@docusaurus/plugin-content-docs').SidebarsConfig} */
const sidebars = {
  guide: [
    'guide/introduction',
    'guide/getting-started',
    'guide/packages',
    {
      type: 'category',
      label: 'Using N+1',
      collapsed: false,
      items: [
        'guide/analyzer',
        {type: 'doc', id: 'runtime', label: 'The runtime guard'},
        'guide/configuration',
        'guide/ci',
        'guide/fixing',
      ],
    },
    {
      type: 'category',
      label: 'Rules',
      collapsed: false,
      items: [
        {type: 'doc', id: 'rules/NP0001', label: 'NP0001 Query per element'},
        {type: 'doc', id: 'rules/NP0002', label: 'NP0002 Lazy load per element'},
        {type: 'doc', id: 'rules/NP0003', label: 'NP0003 Save per element'},
        {type: 'doc', id: 'rules/NP0004', label: 'NP0004 Recursive query'},
        {type: 'doc', id: 'rules/NP0005', label: 'NP0005 Serialized lazy entities'},
        {type: 'doc', id: 'rules/NP0006', label: 'NP0006 Hidden data access'},
        {type: 'doc', id: 'rules/NP0007', label: 'NP0007 Convention mapping'},
      ],
    },
    {
      type: 'category',
      label: 'Under the hood',
      items: [
        {type: 'doc', id: 'design', label: 'How it works'},
        {type: 'doc', id: 'catalogue', label: 'Case catalogue'},
        {type: 'doc', id: 'comparison', label: 'Comparison with other tools'},
      ],
    },
    'guide/faq',
  ],
};

export default sidebars;
