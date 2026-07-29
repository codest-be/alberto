import type { CodegenConfig } from "@graphql-codegen/cli";

const config: CodegenConfig = {
  // Point at the running Orders API (via Vite proxy or direct)
  schema: "http://localhost:5163/graphql",
  documents: ["src/**/*.graphql"],
  ignoreNoDocuments: true,
  generates: {
    "src/graphql/generated.ts": {
      plugins: [
        "typescript",
        "typescript-operations",
        "typescript-react-apollo",
      ],
      config: {
        withHooks: true,
        withComponent: false,
        withHOC: false,
        scalars: {
          DateTime: "string",
          Long: "number",
          UUID: "string",
        },
      },
    },
  },
};

export default config;
