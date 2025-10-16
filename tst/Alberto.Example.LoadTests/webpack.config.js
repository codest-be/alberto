const path = require('path');

module.exports = {
  mode: 'production',
  entry: {
    'order-lifecycle.test': './src/order-lifecycle.test.ts'
  },
  output: {
    path: path.resolve(__dirname, 'dist'),
    filename: '[name].js',
    libraryTarget: 'commonjs',
  },
  resolve: {
    extensions: ['.ts', '.js'],
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
  externals: /^(k6|https?\:\/\/)(\/.*)?/,
  devtool: 'source-map',
  stats: {
    colors: true,
  },
  optimization: {
    minimize: false,
  },
};
