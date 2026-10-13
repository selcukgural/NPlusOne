import clsx from 'clsx';
import Link from '@docusaurus/Link';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';
import CodeBlock from '@theme/CodeBlock';

import styles from './index.module.css';

const warning = `OrderService.cs(42,31): warning NP0001: 'IOrderRepository.GetByCustomerAsync' runs a query
  ('ToListAsync' in OrderRepository.GetByCustomerAsync) once per element of the foreach loop over 'customers'.
  Load the rows for all elements up front with one query, Where(o => keys.Contains(o.CustomerId)),
  and group them with ToLookup(o => o.CustomerId).`;

const features = [
  {
    title: 'At build time',
    text: 'A Roslyn analyzer follows queries, lazy loads and saves through loops, LINQ lambdas, services, interfaces, mediators, mappers and serializers, and reports the ones that run once per element. Precision first: it warns only when it can prove the round trip.',
  },
  {
    title: 'In tests and requests',
    text: 'A runtime guard watches the SQL EF Core really sends inside a scope and fails a test, or flags a request, when the same query or lazy load repeats. It catches what no compiler can see.',
  },
  {
    title: 'Proven on a real database',
    text: 'Every one of the 142 known N+1 shapes in the catalogue is a test case run on SQLite: a reported case must send more SQL for more rows, a silent one the same amount, on EF Core 8, 9 and 10.',
  },
];

export default function Home() {
  const {siteConfig} = useDocusaurusContext();
  return (
    <Layout title="Find N+1 queries in .NET" description={siteConfig.tagline}>
      <header className={clsx('hero hero--primary', styles.heroBanner)}>
        <div className="container">
          <Heading as="h1" className="hero__title">{siteConfig.title}</Heading>
          <p className="hero__subtitle">{siteConfig.tagline}</p>
          <div className={styles.buttons}>
            <Link className="button button--secondary button--lg" to="/docs/guide/getting-started">
              Get started in five minutes
            </Link>
          </div>
        </div>
      </header>
      <main className="container margin-vert--lg">
        <CodeBlock language="bash">{'dotnet add package NPlusOne.Analyzers'}</CodeBlock>
        <CodeBlock language="text">{warning}</CodeBlock>
        <div className="row margin-top--lg">
          {features.map((feature) => (
            <div key={feature.title} className="col col--4">
              <Heading as="h3">{feature.title}</Heading>
              <p>{feature.text}</p>
            </div>
          ))}
        </div>
      </main>
    </Layout>
  );
}
