// Documentation site for 3MF Library, published to www.3mf-library.com by .github/workflows/docs.yml.
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  site: 'https://www.3mf-library.com',
  integrations: [
    starlight({
      title: '3MF Library',
      description: 'A self-hosted preview site for your 3D print files: browse, preview, search and open 3MF and STL files in your slicer.',
      logo: { src: './src/assets/logo.svg' },
      favicon: '/favicon.svg',
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/jurjen74/3mf-library' }],
      editLink: { baseUrl: 'https://github.com/jurjen74/3mf-library/edit/main/docs/' },
      customCss: ['./src/styles/custom.css'],
      head: [
        { tag: 'link', attrs: { rel: 'preload', href: '/fonts/inter-latin-wght-normal.woff2', as: 'font', type: 'font/woff2', crossorigin: '' } },
      ],
      sidebar: [
        {
          label: 'Start here',
          items: [
            { label: 'What it is', slug: 'start/introduction' },
            { label: 'Quick start', slug: 'start/quick-start' },
            { label: 'The first account', slug: 'start/first-account' },
            { label: 'A tour of the site', slug: 'start/tour' },
          ],
        },
        {
          label: 'Installation',
          items: [
            { label: 'Choose your setup', slug: 'install/overview' },
            { label: 'Docker Compose on Linux', slug: 'install/linux' },
            { label: 'Mac or Windows PC', slug: 'install/desktop' },
            { label: 'Synology', slug: 'install/synology' },
            { label: 'Unraid', slug: 'install/unraid' },
            { label: 'TrueNAS SCALE', slug: 'install/truenas' },
            { label: 'Raspberry Pi', slug: 'install/raspberry-pi' },
            { label: 'Proxmox', slug: 'install/proxmox' },
            { label: 'Portainer', slug: 'install/portainer' },
            { label: 'VPS with HTTPS', slug: 'install/vps' },
          ],
        },
        {
          label: 'Guides',
          items: [
            { label: 'Library on a NAS', slug: 'guides/nas' },
            { label: 'Reverse proxy and HTTPS', slug: 'guides/reverse-proxy' },
            { label: 'Accounts and users', slug: 'guides/accounts' },
            { label: 'Open in your slicer', slug: 'guides/slicers' },
            { label: 'Open the local copy', slug: 'guides/local-copy' },
            { label: 'Phones and tablets', slug: 'guides/phones' },
            { label: 'Updating and backups', slug: 'guides/updating' },
          ],
        },
        {
          label: 'Reference',
          items: [
            { label: 'Configuration', slug: 'reference/configuration' },
            { label: 'Supported files', slug: 'reference/files' },
            { label: 'Security', slug: 'reference/security' },
            { label: 'Troubleshooting', slug: 'reference/troubleshooting' },
          ],
        },
        {
          label: 'Project',
          items: [
            { label: 'Development', slug: 'project/development' },
            { label: 'Credits and licences', slug: 'project/credits' },
          ],
        },
      ],
    }),
  ],
});
