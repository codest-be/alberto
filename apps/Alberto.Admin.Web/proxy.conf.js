// Log all service-related environment variables for debugging
console.log('[Proxy] Environment variables:');
Object.keys(process.env)
  .filter(k => k.startsWith('services__'))
  .forEach(k => console.log(`  ${k}=${process.env[k]}`));

const target = process.env['services__orders-api__https__0']
  || process.env['services__orders-api__http__0']
  || 'http://localhost:5180';

console.log('[Proxy] Using target:', target);

// Single proxy config that handles both HTTP and WebSocket for /graphql
const PROXY_CONFIG = {
  '/alberto': {
    target,
    secure: false,
    changeOrigin: true,
    logLevel: 'debug',
  },
  '/graphql': {
    target,
    secure: false,
    changeOrigin: true,
    ws: true,
    logLevel: 'debug',
  },
};

module.exports = PROXY_CONFIG;
