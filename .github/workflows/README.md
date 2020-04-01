# GitHub Actions vs Azure Pipelines

- Batching of builds in not supported in GitHub Actions.
In Azure Pipelines it is possible to batch builds using `batch` keyword; this feature is [not supported](https://github.community/t5/GitHub-Actions/How-to-batch-actions/td-p/43992) in GitHub Actions.

- `continue-on-error` option is [supported on step level](https://github.community/t5/GitHub-Actions/continue-on-error-allow-failure-UI-indication/td-p/37033), not on job level.

- `if` conditions in GitHubActions are supported on job and step level. However, on job level it seems not possible to access environment variables. The following syntax:
```
jobs:
  build_and_test:
    name: Build and Test Solution
    runs-on: ubuntu-16.04
    if: env.RunTestsOnly != 'true'
```
causes an error in pipeline validation:
```
Your workflow file was invalid: The pipeline is not valid. .github/workflows/unit_test.yml (Line: 60, Col: 9): Unrecognized named-value: 'env'.
```
but works fine if the same condition is inside a step.

- Azure Pipelines provide an overview with results of tests ran during the workflow, including stack trace of failed tests and code coverage. GitHub Actions currently [do not support this](https://github.community/t5/GitHub-Actions/Publishing-Test-Results/td-p/31242).

- GutHub Actions do not allow starting a pipeline run for a selected branch, there needs to be a trigger defined in the workflow file, such as push or pull request.

- Jobs in GitHub Actions [cannot use dependencies](https://github.community/t5/GitHub-Actions/How-do-I-specify-job-dependency-running-in-another-workflow/td-p/33938) from other workflow files.
