const path = require('path');

module.exports = {
  mode: 'production',
  entry: {
    'smoke.test': './src/tests/smoke.test.ts',
    'load.test': './src/tests/load.test.ts',
    'stress.test': './src/tests/stress.test.ts',
    'spike.test': './src/tests/spike.test.ts',
    'consistency.test': './src/tests/consistency.test.ts',
    'throughput.test': './src/tests/throughput.test.ts',
  },
  output: {
    path: path.resolve(__dirname, 'dist'),
    filename: '[name].js',
    libraryTarget: 'commonjs',
  },
  resolve: {
    extensions: ['.ts', '.js'],
    alias: {
      '@config': path.resolve(__dirname, 'src/config'),
      '@graphql': path.resolve(__dirname, 'src/graphql'),
      '@lib': path.resolve(__dirname, 'src/lib'),
      '@scenarios': path.resolve(__dirname, 'src/scenarios'),
    },
  },
  module: {
    rules: [
      {
        test: /\.ts$/,
        use: 'ts-loader',
        exclude: /node_modules/,
      },
    ],
  },
  target: 'web',
  externals: /^k6(\/.*)?$/,
  optimization: {
    minimize: false,
  },
};
