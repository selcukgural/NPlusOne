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
    text: 'Every one of the 143 known N+1 shapes in the catalogue is a test case run on SQLite: a reported case must send more SQL for more rows, a silent one the same amount, on EF Core 8, 9 and 10.',
  },
];

// One package per place an N+1 can be caught; each card gives the install command and where to read on.
const packages = [
  {
    id: 'NPlusOne.Analyzers',
    when: 'Build time',
    text: 'The Roslyn analyzer, its code fixes and the generator that follows calls across projects. A development dependency: nothing ships with your app.',
    docs: '/docs/guide/analyzer',
  },
  {
    id: 'NPlusOne.EntityFrameworkCore',
    when: 'Tests',
    text: 'The runtime guard: UseNPlusOneGuard() on the DbContext, a scope per test, AssertNoNPlusOne(). Any test framework, no DI needed.',
    docs: '/docs/runtime',
  },
  {
    id: 'NPlusOne.AspNetCore',
    when: 'Requests',
    text: 'One guard scope per HTTP request, [NPlusOneAssert] for MVC and .WithNPlusOneAssert() for minimal APIs. Brings the guard with it.',
    docs: '/docs/runtime#in-aspnet-core',
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
            <Link className={clsx('button button--outline button--lg', styles.heroOutline)} to="/docs/guide/packages">
              Choose your packages
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
        <section className="margin-top--lg">
          <Heading as="h2">Three packages</Heading>
          <p>
            Released together under one version, all on{' '}
            <Link href="https://www.nuget.org/packages?q=owner%3Aselcukgural+NPlusOne">NuGet</Link>. Use the analyzer,
            the guard, or both.
          </p>
          <div className="row">
            {packages.map((item) => (
              <div key={item.id} className="col col--4 margin-bottom--md">
                <div className={clsx('card', styles.package)}>
                  <div className="card__header">
                    <span className="badge badge--primary">{item.when}</span>
                    <Heading as="h3" className={styles.packageName}>{item.id}</Heading>
                  </div>
                  <div className="card__body">
                    <p>{item.text}</p>
                    <CodeBlock language="bash">{`dotnet add package ${item.id}`}</CodeBlock>
                  </div>
                  <div className="card__footer">
                    <Link className="button button--primary button--sm margin-right--sm" href={`https://www.nuget.org/packages/${item.id}`}>
                      NuGet
                    </Link>
                    <Link className="button button--secondary button--sm" to={item.docs}>
                      Docs
                    </Link>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </section>
      </main>
    </Layout>
  );
}
