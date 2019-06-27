using System.Collections.Generic;
using System.Linq;
using AElf.Contracts.MultiToken.Messages;
using AElf.Contracts.ParliamentAuth;
using AElf.Contracts.TokenConverter;
using AElf.Sdk.CSharp;
using AElf.Types;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.Economic
{
    public class EconomicContract : EconomicContractContainer.EconomicContractBase
    {
        public override Empty InitialEconomicSystem(InitialEconomicSystemInput input)
        {
            Assert(!State.Initialized.Value, "Already initialized.");

            State.TokenContract.Value =
                Context.GetContractAddressByName(SmartContractConstants.TokenContractSystemName);

            CreateNativeToken(input);
            CreateTokenConverterToken();
            CreateResourceTokens();
            CreateMiningToken();
            CreateElectionToken();

            InitialMiningReward(input.MiningRewardTotalAmount);

            RegisterElectionVotingEvent();

            InitializeTokenConverterContract();

            State.Initialized.Value = true;
            return new Empty();
        }

        private void CreateNativeToken(InitialEconomicSystemInput input)
        {
            State.TokenContract.Create.Send(new CreateInput
            {
                Symbol = input.NativeTokenSymbol,
                TotalSupply = input.NativeTokenTotalSupply,
                Decimals = input.NativeTokenDecimals,
                IsBurnable = input.IsNativeTokenBurnable,
                Issuer = Context.Self,
                LockWhiteList =
                {
                    Context.GetContractAddressByName(SmartContractConstants.VoteContractSystemName),
                    Context.GetContractAddressByName(SmartContractConstants.ProfitContractSystemName),
                    Context.GetContractAddressByName(SmartContractConstants.ElectionContractSystemName),
                    Context.GetContractAddressByName(SmartContractConstants.TreasuryContractSystemName)
                }
            });
        }

        private void CreateTokenConverterToken()
        {
            State.TokenContract.Create.Send(new CreateInput
            {
                Symbol = EconomicContractConstants.TokenConverterTokenSymbol,
                TokenName = "AElf Token Converter Token",
                TotalSupply = EconomicContractConstants.TokenConverterTokenTotalSupply,
                Decimals = EconomicContractConstants.TokenConverterTokenDecimals,
                Issuer = Context.GetContractAddressByName(SmartContractConstants.TokenConverterContractSystemName),
                IsBurnable = true,
                LockWhiteList =
                {
                    Context.GetContractAddressByName(SmartContractConstants.ProfitContractSystemName),
                    Context.GetContractAddressByName(SmartContractConstants.TreasuryContractSystemName)
                }
            });
        }

        private void CreateResourceTokens()
        {
            foreach (var resourceTokenSymbol in EconomicContractConstants.ResourceTokenSymbols)
            {
                State.TokenContract.Create.Send(new CreateInput
                {
                    Symbol = resourceTokenSymbol,
                    TokenName = $"{resourceTokenSymbol} Token",
                    TotalSupply = EconomicContractConstants.ResourceTokenTotalSupply,
                    Decimals = EconomicContractConstants.ResourceTokenDecimals,
                    Issuer = Context.Self,
                    IsBurnable = true // TODO: TBD,

                });
            }
        }

        private void CreateMiningToken()
        {
            State.TokenContract.Create.Send(new CreateInput
            {
                Symbol = EconomicContractConstants.MiningTokenSymbol,
                TokenName = "Mining Token",
                TotalSupply = EconomicContractConstants.MiningTokenTotalSupply,
                Decimals = 0,
                Issuer = Context.GetContractAddressByName(SmartContractConstants.ConsensusContractSystemName),
                IsBurnable = true,
                IsTransferDisabled = true,
                LockWhiteList = {Context.GetContractAddressByName(SmartContractConstants.ConsensusContractSystemName)}
            });
        }

        private void CreateElectionToken()
        {
            State.TokenContract.Create.Send(new CreateInput
            {
                Symbol = EconomicContractConstants.ElectionTokenSymbol,
                TokenName = "Election Token",
                TotalSupply = EconomicContractConstants.ElectionTokenTotalSupply,
                Decimals = 0,
                Issuer = Context.GetContractAddressByName(SmartContractConstants.ElectionContractSystemName),
                IsBurnable = false,
                IsTransferDisabled = true
            });
        }

        public override Empty IssueNativeToken(IssueNativeTokenInput input)
        {
            var nativeTokenInfo = State.TokenContract.GetTokenInfo.Call(new GetTokenInfoInput
            {
                Symbol = Context.Variables.NativeSymbol
            });
            if (Context.Sender != nativeTokenInfo.Issuer)
            {
                return new Empty();
            }

            State.TokenContract.Issue.Send(new IssueInput
            {
                Symbol = Context.Variables.NativeSymbol,
                Amount = input.Amount,
                To = input.To,
                Memo = input.Memo
            });
            return new Empty();
        }

        /// <summary>
        /// Transfer all the tokens prepared for rewarding mining to consensus contract.
        /// </summary>
        /// <param name="miningRewardAmount"></param>
        private void InitialMiningReward(long miningRewardAmount)
        {
            var consensusContractAddress =
                Context.GetContractAddressByName(SmartContractConstants.ConsensusContractSystemName);

            State.TokenContract.Issue.Send(new IssueInput
            {
                To = consensusContractAddress,
                Amount = miningRewardAmount,
                Symbol = Context.Variables.NativeSymbol,
                Memo = "Initial mining reward."
            });
        }

        private void RegisterElectionVotingEvent()
        {
            State.ElectionContract.Value =
                Context.GetContractAddressByName(SmartContractConstants.ElectionContractSystemName);
            State.ElectionContract.RegisterElectionVotingEvent.Send(new Empty());
        }

        private Address CreateConnectorManager()
        {
            State.ParliamentAuthContract.Value =
                Context.GetContractAddressByName(SmartContractConstants.ParliamentAuthContractSystemName);

            var createOrganizationInput = new CreateOrganizationInput {ReleaseThreshold = 1};
            State.ParliamentAuthContract.CreateOrganization.Send(createOrganizationInput);

            var organizationHash = Hash.FromTwoHashes(Hash.FromMessage(State.ParliamentAuthContract.Value),
                Hash.FromMessage(createOrganizationInput));
            return Address.FromPublicKey(State.ParliamentAuthContract.Value.Value.Concat(
                organizationHash.Value.ToByteArray().CalculateHash()).ToArray());
        }

        private void InitializeTokenConverterContract()
        {
            var connectorManager = CreateConnectorManager();
            State.TokenConverterContract.Value =
                Context.GetContractAddressByName(SmartContractConstants.TokenConverterContractSystemName);
            var connectors = new List<Connector>
            {
                new Connector
                {
                    Symbol = Context.Variables.NativeSymbol,
                    IsPurchaseEnabled = true,
                    IsVirtualBalanceEnabled = true,
                    Weight = "0.5",
                    VirtualBalance = 0,
                },
                new Connector
                {
                    Symbol = EconomicContractConstants.TokenConverterTokenSymbol,
                    IsPurchaseEnabled = true,
                    IsVirtualBalanceEnabled = true,
                    Weight = "0.5",
                    VirtualBalance = EconomicContractConstants.TokenConverterTokenConnectorInitialVirtualBalance
                },
            };
            foreach (var resourceTokenSymbol in EconomicContractConstants.ResourceTokenSymbols)
            {
                connectors.Add(new Connector
                {
                    Symbol = resourceTokenSymbol,
                    IsPurchaseEnabled = true,
                    IsVirtualBalanceEnabled = true,
                    Weight = EconomicContractConstants.ResourceTokenConnectorWeight,
                    VirtualBalance = EconomicContractConstants.ResourceTokenConnectorInitialVirtualBalance
                });
            }

            State.TokenConverterContract.Initialize.Send(new InitializeInput
            {
                FeeRate = EconomicContractConstants.TokenConverterFeeRate,
                Connectors = {connectors},
                BaseTokenSymbol = Context.Variables.NativeSymbol,
                ManagerAddress = connectorManager
            });
        }
    }
}