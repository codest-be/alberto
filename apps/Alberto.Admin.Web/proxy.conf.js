const target = process.env['services__orders-api__https__0']
  || process.env['services__orders-api__http__0']
  || 'http://localhost:5180';

console.log('[Proxy] Target:', target);

const PROXY_CONFIG = [
  {
    context: ['/alberto'],
    target,
    secure: false,
    changeOrigin: true,
    logLevel: 'debug',
  },
  {
    context: ['/graphql'],
    target,
    secure: false,
    changeOrigin: true,
    ws: true,
    logLevel: 'debug',
  },
];

module.exports = PROXY_CONFIG;
