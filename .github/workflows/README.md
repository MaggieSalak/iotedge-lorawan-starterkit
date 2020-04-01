# GitHub Actions vs Azure Pipelines

- Batching of builds in not supported in GitHub Actions.
In Azure Pipelines it is possible to batch builds using `batch` keyword; this feature is [not supported](https://github.community/t5/GitHub-Actions/How-to-batch-actions/td-p/43992) in GitHub Actions.

- `continue-on-error` option is [supported on step level](https://github.community/t5/GitHub-Actions/continue-on-error-allow-failure-UI-indication/td-p/37033), not on job level.

- `if` conditions in GitHubActions are supported on job and step level. However, on job level it seems not possible to access environment variables. The following syntax:
```

```
causes an error in pipeline validation:
`Your workflow file was invalid: The pipeline is not valid. .github/workflows/unit_test.yml (Line: 60, Col: 9): Unrecognized named-value: 'env'.`
