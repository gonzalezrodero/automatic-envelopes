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
            "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com"
            "token.actions.githubusercontent.com:ref" = "refs/heads/main"
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

resource "aws_iam_policy" "github_deploy" {
  name = "${var.project_name}-github-deploy"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "ProjectBuckets"
        Effect = "Allow"
        Action = "s3:*"
        Resource = [
          "arn:aws:s3:::${var.project_name}-*",
          "arn:aws:s3:::${var.project_name}-*/*",
        ]
      },
      {
        Sid      = "ListBuckets"
        Effect   = "Allow"
        Action   = "s3:ListAllMyBuckets"
        Resource = "*"
      },
      {
        Sid    = "IamRoles"
        Effect = "Allow"
        Action = [
          "iam:CreateRole",
          "iam:DeleteRole",
          "iam:GetRole",
          "iam:UpdateRole",
          "iam:UpdateAssumeRolePolicy",
          "iam:TagRole",
          "iam:UntagRole",
          "iam:ListRoleTags",
          "iam:ListAttachedRolePolicies",
          "iam:ListRolePolicies",
          "iam:ListInstanceProfilesForRole",
          "iam:PutRolePolicy",
          "iam:GetRolePolicy",
          "iam:DeleteRolePolicy",
        ]
        Resource = "arn:aws:iam::${var.aws_account_id}:role/${var.project_name}-*"
      },
      {
        Sid    = "IamAttachKnownPolicies"
        Effect = "Allow"
        Action = [
          "iam:AttachRolePolicy",
          "iam:DetachRolePolicy",
        ]
        Resource = "arn:aws:iam::${var.aws_account_id}:role/${var.project_name}-*"
        Condition = {
          ArnEquals = {
            "iam:PolicyARN" = [
              "arn:aws:iam::aws:policy/ReadOnlyAccess",
              "arn:aws:iam::aws:policy/service-role/AWSLambdaSQSQueueExecutionRole",
              "arn:aws:iam::aws:policy/service-role/AmazonRDSEnhancedMonitoringRole",
              "arn:aws:iam::${var.aws_account_id}:policy/${var.project_name}-bedrock-standard-access",
              "arn:aws:iam::${var.aws_account_id}:policy/${var.project_name}-github-deploy",
              "arn:aws:iam::${var.aws_account_id}:policy/${var.project_name}-github-deploy-services",
            ]
          }
        }
      },
      {
        Sid      = "IamPassRole"
        Effect   = "Allow"
        Action   = "iam:PassRole"
        Resource = "arn:aws:iam::${var.aws_account_id}:role/${var.project_name}-*"
        Condition = {
          StringEquals = {
            "iam:PassedToService" = [
              "lambda.amazonaws.com",
              "scheduler.amazonaws.com",
              "monitoring.rds.amazonaws.com",
              "ecs-tasks.amazonaws.com",
            ]
          }
        }
      },
      {
        Sid    = "IamProjectPolicies"
        Effect = "Allow"
        Action = [
          "iam:CreatePolicy",
          "iam:DeletePolicy",
          "iam:GetPolicy",
          "iam:GetPolicyVersion",
          "iam:ListPolicyVersions",
          "iam:CreatePolicyVersion",
          "iam:DeletePolicyVersion",
          "iam:SetDefaultPolicyVersion",
          "iam:TagPolicy",
          "iam:UntagPolicy",
          "iam:ListPolicyTags",
          "iam:ListEntitiesForPolicy",
        ]
        Resource = "arn:aws:iam::${var.aws_account_id}:policy/${var.project_name}-*"
      },
      {
        Sid    = "IamReadAwsPolicies"
        Effect = "Allow"
        Action = [
          "iam:GetPolicy",
          "iam:GetPolicyVersion",
          "iam:ListPolicyVersions",
        ]
        Resource = "arn:aws:iam::aws:policy/*"
      },
      {
        Sid    = "IamOidc"
        Effect = "Allow"
        Action = [
          "iam:CreateOpenIDConnectProvider",
          "iam:DeleteOpenIDConnectProvider",
          "iam:GetOpenIDConnectProvider",
          "iam:UpdateOpenIDConnectProviderThumbprint",
          "iam:AddClientIDToOpenIDConnectProvider",
          "iam:RemoveClientIDFromOpenIDConnectProvider",
          "iam:TagOpenIDConnectProvider",
          "iam:UntagOpenIDConnectProvider",
          "iam:ListOpenIDConnectProviderTags",
        ]
        Resource = "arn:aws:iam::${var.aws_account_id}:oidc-provider/token.actions.githubusercontent.com"
      },
      {
        Sid    = "IamList"
        Effect = "Allow"
        Action = [
          "iam:ListRoles",
          "iam:ListPolicies",
          "iam:ListOpenIDConnectProviders",
        ]
        Resource = "*"
      },
      {
        Sid      = "IamServiceLinkedRole"
        Effect   = "Allow"
        Action   = "iam:CreateServiceLinkedRole"
        Resource = "arn:aws:iam::${var.aws_account_id}:role/aws-service-role/*"
        Condition = {
          StringEquals = {
            "iam:AWSServiceName" = [
              "rds.amazonaws.com",
              "lambda.amazonaws.com",
            ]
          }
        }
      },
    ]
  })
}

resource "aws_iam_policy" "github_deploy_services" {
  name = "${var.project_name}-github-deploy-services"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "Network"
        Effect = "Allow"
        Action = [
          "ec2:Describe*",
          "ec2:CreateVpc",
          "ec2:DeleteVpc",
          "ec2:ModifyVpcAttribute",
          "ec2:AssociateVpcCidrBlock",
          "ec2:DisassociateVpcCidrBlock",
          "ec2:CreateSubnet",
          "ec2:DeleteSubnet",
          "ec2:ModifySubnetAttribute",
          "ec2:CreateInternetGateway",
          "ec2:DeleteInternetGateway",
          "ec2:AttachInternetGateway",
          "ec2:DetachInternetGateway",
          "ec2:CreateRouteTable",
          "ec2:DeleteRouteTable",
          "ec2:CreateRoute",
          "ec2:DeleteRoute",
          "ec2:ReplaceRoute",
          "ec2:AssociateRouteTable",
          "ec2:DisassociateRouteTable",
          "ec2:CreateSecurityGroup",
          "ec2:DeleteSecurityGroup",
          "ec2:AuthorizeSecurityGroupIngress",
          "ec2:AuthorizeSecurityGroupEgress",
          "ec2:RevokeSecurityGroupIngress",
          "ec2:RevokeSecurityGroupEgress",
          "ec2:UpdateSecurityGroupRuleDescriptionsIngress",
          "ec2:UpdateSecurityGroupRuleDescriptionsEgress",
          "ec2:ModifySecurityGroupRules",
          "ec2:CreateNetworkAcl",
          "ec2:DeleteNetworkAcl",
          "ec2:CreateNetworkAclEntry",
          "ec2:DeleteNetworkAclEntry",
          "ec2:ReplaceNetworkAclEntry",
          "ec2:ReplaceNetworkAclAssociation",
          "ec2:CreateDhcpOptions",
          "ec2:DeleteDhcpOptions",
          "ec2:AssociateDhcpOptions",
          "ec2:CreateTags",
          "ec2:DeleteTags",
        ]
        Resource = "*"
        Condition = {
          StringEquals = {
            "aws:RequestedRegion" = var.aws_region
          }
        }
      },
      {
        Sid      = "Rds"
        Effect   = "Allow"
        Action   = "rds:*"
        Resource = "*"
        Condition = {
          StringEquals = {
            "aws:RequestedRegion" = var.aws_region
          }
        }
      },
      {
        Sid      = "RdsManagedSecret"
        Effect   = "Allow"
        Action   = "secretsmanager:*"
        Resource = "arn:aws:secretsmanager:${var.aws_region}:${var.aws_account_id}:secret:rds!*"
      },
      {
        Sid      = "ListSecrets"
        Effect   = "Allow"
        Action   = "secretsmanager:ListSecrets"
        Resource = "*"
      },
      {
        Sid    = "Lambda"
        Effect = "Allow"
        Action = [
          "lambda:*",
        ]
        Resource = [
          "arn:aws:lambda:${var.aws_region}:${var.aws_account_id}:function:${var.project_name}-*",
          "arn:aws:lambda:${var.aws_region}:${var.aws_account_id}:function:${var.project_name}-*:*",
          "arn:aws:lambda:${var.aws_region}:${var.aws_account_id}:event-source-mapping:*",
        ]
      },
      {
        Sid    = "LambdaList"
        Effect = "Allow"
        Action = [
          "lambda:ListFunctions",
          "lambda:ListEventSourceMappings",
          "lambda:GetEventSourceMapping",
        ]
        Resource = "*"
        Condition = {
          StringEquals = {
            "aws:RequestedRegion" = var.aws_region
          }
        }
      },
      {
        Sid    = "Sqs"
        Effect = "Allow"
        Action = "sqs:*"
        Resource = [
          "arn:aws:sqs:${var.aws_region}:${var.aws_account_id}:${var.project_name}-*",
          "arn:aws:sqs:${var.aws_region}:${var.aws_account_id}:wolverine-dead-letter-queue",
        ]
      },
      {
        Sid      = "SqsList"
        Effect   = "Allow"
        Action   = "sqs:ListQueues"
        Resource = "*"
        Condition = {
          StringEquals = {
            "aws:RequestedRegion" = var.aws_region
          }
        }
      },
      {
        Sid    = "Ecr"
        Effect = "Allow"
        Action = [
          "ecr:CreateRepository",
          "ecr:DeleteRepository",
          "ecr:DescribeRepositories",
          "ecr:ListTagsForResource",
          "ecr:TagResource",
          "ecr:UntagResource",
          "ecr:PutImageScanningConfiguration",
          "ecr:PutImageTagMutability",
          "ecr:PutLifecyclePolicy",
          "ecr:GetLifecyclePolicy",
          "ecr:DeleteLifecyclePolicy",
          "ecr:GetRepositoryPolicy",
          "ecr:SetRepositoryPolicy",
          "ecr:DeleteRepositoryPolicy",
          "ecr:BatchCheckLayerAvailability",
          "ecr:InitiateLayerUpload",
          "ecr:UploadLayerPart",
          "ecr:CompleteLayerUpload",
          "ecr:PutImage",
          "ecr:BatchGetImage",
          "ecr:GetDownloadUrlForLayer",
          "ecr:DescribeImages",
          "ecr:ListImages",
          "ecr:BatchDeleteImage",
        ]
        Resource = "arn:aws:ecr:${var.aws_region}:${var.aws_account_id}:repository/${var.project_name}-api"
      },
      {
        Sid      = "EcrAuth"
        Effect   = "Allow"
        Action   = "ecr:GetAuthorizationToken"
        Resource = "*"
      },
      {
        Sid    = "KmsForEcr"
        Effect = "Allow"
        Action = [
          "kms:CreateGrant",
          "kms:DescribeKey",
          "kms:Decrypt",
          "kms:GenerateDataKey",
        ]
        Resource = "*"
        Condition = {
          StringEquals = {
            "kms:ViaService" = "ecr.${var.aws_region}.amazonaws.com"
          }
        }
      },
      {
        Sid      = "Cognito"
        Effect   = "Allow"
        Action   = "cognito-idp:*"
        Resource = "*"
        Condition = {
          StringEquals = {
            "aws:RequestedRegion" = var.aws_region
          }
        }
      },
      {
        Sid    = "LambdaLogs"
        Effect = "Allow"
        Action = [
          "logs:CreateLogGroup",
          "logs:DeleteLogGroup",
          "logs:PutRetentionPolicy",
          "logs:DeleteRetentionPolicy",
          "logs:ListTagsForResource",
          "logs:TagResource",
          "logs:UntagResource",
          "logs:ListTagsLogGroup",
          "logs:TagLogGroup",
          "logs:UntagLogGroup",
        ]
        Resource = [
          "arn:aws:logs:${var.aws_region}:${var.aws_account_id}:log-group:/aws/lambda/${var.project_name}-*",
          "arn:aws:logs:${var.aws_region}:${var.aws_account_id}:log-group:/aws/lambda/${var.project_name}-*:*",
        ]
      },
      {
        Sid      = "DescribeLogGroups"
        Effect   = "Allow"
        Action   = "logs:DescribeLogGroups"
        Resource = "*"
        Condition = {
          StringEquals = {
            "aws:RequestedRegion" = var.aws_region
          }
        }
      },
      {
        Sid    = "Scheduler"
        Effect = "Allow"
        Action = "scheduler:*"
        Resource = [
          "arn:aws:scheduler:${var.aws_region}:${var.aws_account_id}:schedule-group/${var.project_name}-rds",
          "arn:aws:scheduler:${var.aws_region}:${var.aws_account_id}:schedule/${var.project_name}-rds/*",
        ]
      },
      {
        Sid    = "SchedulerList"
        Effect = "Allow"
        Action = [
          "scheduler:ListSchedules",
          "scheduler:ListScheduleGroups",
        ]
        Resource = "*"
        Condition = {
          StringEquals = {
            "aws:RequestedRegion" = var.aws_region
          }
        }
      },
      {
        Sid      = "CloudFront"
        Effect   = "Allow"
        Action   = "cloudfront:*"
        Resource = "*"
      },
      {
        Sid      = "Acm"
        Effect   = "Allow"
        Action   = "acm:*"
        Resource = "*"
        Condition = {
          StringEquals = {
            "aws:RequestedRegion" = "us-east-1"
          }
        }
      },
      {
        Sid      = "Route53"
        Effect   = "Allow"
        Action   = "route53:*"
        Resource = "*"
      },
    ]
  })
}

moved {
  from = aws_iam_role_policy_attachment.admin_attach
  to   = aws_iam_role_policy_attachment.github_deploy
}

resource "aws_iam_role_policy_attachment" "github_deploy_services" {
  role       = aws_iam_role.github_actions.name
  policy_arn = aws_iam_policy.github_deploy_services.arn
}

resource "aws_iam_role_policy_attachment" "github_deploy" {
  role       = aws_iam_role.github_actions.name
  policy_arn = aws_iam_policy.github_deploy.arn

  # Attach the service policy first, then replace AdministratorAccess.
  depends_on = [aws_iam_role_policy_attachment.github_deploy_services]

  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_iam_role" "github_actions_plan" {
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
            "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com"
            "token.actions.githubusercontent.com:sub" = "repo:${var.github_owner}/${var.github_repo}:pull_request"
          }
        }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "github_plan_readonly" {
  role       = aws_iam_role.github_actions_plan.name
  policy_arn = "arn:aws:iam::aws:policy/ReadOnlyAccess"
}

resource "aws_iam_role_policy" "github_plan_state" {
  name = "${var.project_name}-github-plan-state"
  role = aws_iam_role.github_actions_plan.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "StateObject"
        Effect = "Allow"
        Action = [
          "s3:GetObject",
          "s3:PutObject",
        ]
        Resource = "${aws_s3_bucket.tf_state.arn}/*"
      },
      {
        Sid    = "StateLock"
        Effect = "Allow"
        Action = [
          "s3:GetObject",
          "s3:PutObject",
          "s3:DeleteObject",
        ]
        Resource = "${aws_s3_bucket.tf_state.arn}/*.tflock"
      },
      {
        Sid      = "StateList"
        Effect   = "Allow"
        Action   = "s3:ListBucket"
        Resource = aws_s3_bucket.tf_state.arn
      },
    ]
  })
}

# ==========================================
# 3. BEDROCK "STANDARD MODELS" POLICY
# ==========================================
data "aws_caller_identity" "current" {}

resource "aws_iam_policy" "bedrock_standard_models" {
  name        = "${var.project_name}-bedrock-standard-access"
  description = "Allows Claude Sonnet 4.6 and Titan models with marketplace validation"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = "bedrock:InvokeModel"
        Resource = [
          "arn:aws:bedrock:*::foundation-model/anthropic.claude-sonnet-4-6",
          "arn:aws:bedrock:*::inference-profile/eu.anthropic.claude-sonnet-4-6",
          "arn:aws:bedrock:${var.aws_region}::foundation-model/amazon.titan-text-lite-v1",
          "arn:aws:bedrock:${var.aws_region}::foundation-model/amazon.titan-text-express-v1",
          "arn:aws:bedrock:${var.aws_region}::foundation-model/amazon.titan-embed-text-v2:0"
        ]
      },
      {
        Effect   = "Allow"
        Action   = "bedrock:ListFoundationModels"
        Resource = "*"
      },
      {
        Effect = "Allow"
        Action = [
          "aws-marketplace:ViewSubscriptions",
          "aws-marketplace:Subscribe"
        ]
        Resource = "*"
      }
    ]
  })
}

# ==========================================
# 4. ECS TASK ROLE (EL QUE USA LA APP)
# ==========================================
resource "aws_iam_role" "ecs_task_role" {
  name = "${var.project_name}-ecs-task-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRole"
        Effect = "Allow"
        Principal = {
          Service = "ecs-tasks.amazonaws.com"
        }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "attach_bedrock_standard" {
  role       = aws_iam_role.ecs_task_role.name
  policy_arn = aws_iam_policy.bedrock_standard_models.arn
}