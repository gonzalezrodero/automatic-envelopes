# ==========================================
# 1. GITHUB OIDC IDENTITY PROVIDER
# ==========================================
resource "aws_iam_openid_connect_provider" "github" {
  url             = "https://token.actions.githubusercontent.com"
  client_id_list  = ["sts.amazonaws.com"]
  thumbprint_list = ["1c58a3a8518e8759bf075b76b750d4f2df264fcd"]
}

# ==========================================
# 2. IAM ROLE FOR GITHUB ACTIONS
# ==========================================
resource "aws_iam_role" "github_actions" {
  name = "${var.project_name}-github-deploy-role"

  # Apply jobs set environment development or production, so `sub` is the
  # environment form. `ref` still has to be main. PRs use the plan role.
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRoleWithWebIdentity"
        Effect = "Allow"
        Principal = {
          Federated = aws_iam_openid_connect_provider.github.arn
        }
        Condition = {
          StringEquals = {
            "token.actions.githubusercontent.com:aud"           = "sts.amazonaws.com"
            "token.actions.githubusercontent.com:ref"           = "refs/heads/main"
            "token.actions.githubusercontent.com:repository_id" = "1189964517"
            "token.actions.githubusercontent.com:sub" = [
              "repo:${var.github_owner}/${var.github_repo}:environment:development",
              "repo:${var.github_owner}/${var.github_repo}:environment:production",
            ]
          }
        }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "admin_attach" {
  role       = aws_iam_role.github_actions.name
  policy_arn = "arn:aws:iam::aws:policy/AdministratorAccess"
}

resource "aws_iam_role" "github_actions_plan" {
  count = var.create_plan_role ? 1 : 0

  name = "${var.project_name}-github-plan-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRoleWithWebIdentity"
        Effect = "Allow"
        Principal = {
          Federated = aws_iam_openid_connect_provider.github.arn
        }
        Condition = {
          StringEquals = {
            "token.actions.githubusercontent.com:aud"           = "sts.amazonaws.com"
            "token.actions.githubusercontent.com:repository_id" = "1189964517"
            "token.actions.githubusercontent.com:sub"           = "repo:${var.github_owner}/${var.github_repo}:pull_request"
          }
        }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "github_plan_readonly" {
  count = var.create_plan_role ? 1 : 0

  role       = aws_iam_role.github_actions_plan[0].name
  policy_arn = "arn:aws:iam::aws:policy/ReadOnlyAccess"
}

# ReadOnlyAccess already covers the current state object. GetObject on that
# object stays allowed. GetObjectVersion is denied everywhere: old state
# versions from April-May 2026 contain the RDS password.
resource "aws_iam_role_policy" "github_plan_deny_data" {
  count = var.create_plan_role ? 1 : 0

  name = "${var.project_name}-github-plan-deny-data"
  role = aws_iam_role.github_actions_plan[0].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "DenyDataReads"
        Effect = "Deny"
        Action = [
          "ssm:GetParameter*",
          "secretsmanager:GetSecretValue",
          "secretsmanager:BatchGetSecretValue",
          "sqs:ReceiveMessage",
          "logs:GetLogEvents",
          "logs:FilterLogEvents",
          "logs:StartQuery",
          "logs:GetQueryResults",
          "logs:GetLogRecord",
          "logs:StartLiveTail",
          "cloudtrail:LookupEvents",
          "cognito-idp:ListUsers*",
          "cognito-idp:Admin*",
          "ecr:BatchGetImage",
          "ecr:GetDownloadUrlForLayer",
          "rds:DownloadDBLogFilePortion",
          "rds:DownloadCompleteDBLogFile",
          "s3:GetObjectVersion",
        ]
        Resource = "*"
      },
      {
        Sid    = "DenyGetObjectOutsideState"
        Effect = "Deny"
        Action = [
          "s3:GetObject",
        ]
        NotResource = [
          aws_s3_bucket.tf_state.arn,
          "${aws_s3_bucket.tf_state.arn}/*",
        ]
      },
    ]
  })
}
