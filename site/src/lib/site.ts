/** The public repository: every outbound link on the page points into it. */
export const REPO = 'https://github.com/naimulnashid/ai-usage-native';

const file = (path: string) => `${REPO}/blob/main/${path}`;

export const SITE = {
  name: 'AI Usage',
  title: 'AI Usage: Claude Code and Codex usage, counted on your own PC',
  description:
    "A native Windows app that reads Claude Code's and Codex's own transcripts and shows tokens, " +
    'estimated cost and runtime, per project and per day. Nothing leaves the PC.',
  author: 'Md. Naimul Hasan Nashid',
};

export const LINKS = {
  repo: REPO,
  // The latest release page, not a file: asset names carry the version.
  download: `${REPO}/releases/latest`,
  issues: `${REPO}/issues`,
  license: file('LICENSE'),
  security: file('SECURITY.md'),
  source: `${REPO}#build-and-install-from-source`,
  smartAppControl: `${REPO}#download`,
  config: `${REPO}#configuration`,
};
