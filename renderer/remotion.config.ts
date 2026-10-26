import { Config } from '@remotion/cli/config';

// Keep Studio font loading consistent with the programmatic render bundle.
Config.overrideWebpackConfig((config) => ({
    ...config,
    // Development asset names must not contain source paths with a # fragment.
    output: {
        ...config.output,
        ...(config.mode === 'development' ? { assetModuleFilename: 'assets/[contenthash][ext]' } : {})
    },
    experiments: {
        ...config.experiments,
        // Lazy proxy requests interpret # in Windows paths as a URL fragment.
        lazyCompilation: false
    },
    module: {
        ...config.module,
        rules: [...(config.module?.rules ?? []), { test: /\.woff2$/, type: 'asset/resource' }]
    }
}));
